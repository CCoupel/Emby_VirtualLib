using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using VirtualLib.Core;
using VirtualLib.Core.Models;

namespace VirtualLib.Connectors;

public sealed class PlexConnector : IMediaServerConnector
{
    private const int PageSize = 100;
    private const string PlexTokenHeader = "X-Plex-Token";

    private readonly ConnectorConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<PlexConnector> _logger;

    // Token state — populated from ApiKey or obtained via plex.tv auth
    private string _plexToken;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _disposed;

    public string ServerType => ServerTypes.Plex;
    public string ConnectorId => _config.Id;
    public string DisplayName => _config.DisplayName;

    public PlexConnector(
        ConnectorConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger<PlexConnector> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(120);

        _httpClient.DefaultRequestHeaders.Add("X-Plex-Product", "VirtualLib");
        _httpClient.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "virtuallib-plugin");
        _httpClient.DefaultRequestHeaders.Add("X-Plex-Version", "1.0.0");

        _plexToken = config.AuthMode == AuthMode.ApiKey ? config.ApiKey : string.Empty;
        if (!string.IsNullOrEmpty(_plexToken))
            _httpClient.DefaultRequestHeaders.Add(PlexTokenHeader, _plexToken);
    }

    // -------------------------------------------------------------------------
    // Authentication (UserCredentials mode via plex.tv)
    // -------------------------------------------------------------------------

    private async Task EnsureAuthenticatedAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (_config.AuthMode != AuthMode.UserCredentials) return;
        if (!string.IsNullOrEmpty(_plexToken) && !forceRefresh) return;

        await _authLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_plexToken) && !forceRefresh) return;

            _logger.LogInformation("Authenticating via plex.tv for connector {ConnectorId}", ConnectorId);

            using var plexTvClient = new HttpClient();
            plexTvClient.DefaultRequestHeaders.Add("X-Plex-Product", "VirtualLib");
            plexTvClient.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "virtuallib-plugin");
            plexTvClient.DefaultRequestHeaders.Add("X-Plex-Version", "1.0.0");
            plexTvClient.DefaultRequestHeaders.Add("Accept", "application/xml");

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("user[login]", _config.Username),
                new KeyValuePair<string, string>("user[password]", _config.Password)
            });

            using var response = await plexTvClient.PostAsync("https://plex.tv/users/sign_in.xml", form, ct);
            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            var authToken = doc.Root?.Attribute("authToken")?.Value;

            if (string.IsNullOrEmpty(authToken))
                throw new InvalidOperationException("Empty authToken in plex.tv response");

            _plexToken = authToken;

            if (_httpClient.DefaultRequestHeaders.Contains(PlexTokenHeader))
                _httpClient.DefaultRequestHeaders.Remove(PlexTokenHeader);
            _httpClient.DefaultRequestHeaders.Add(PlexTokenHeader, _plexToken);

            _logger.LogInformation("Authenticated via plex.tv for connector {ConnectorId}", ConnectorId);
        }
        finally
        {
            _authLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // HTTP helpers
    // -------------------------------------------------------------------------

    private async Task<XDocument?> GetXmlAsync(string url, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized
            && _config.AuthMode == AuthMode.UserCredentials)
        {
            response.Dispose();
            _logger.LogDebug("401 on {Url} — re-authenticating", url);
            await EnsureAuthenticatedAsync(ct, forceRefresh: true);
            response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);
            return XDocument.Parse(content);
        }
    }

    private async Task<HttpResponseMessage> GetStreamResponseAsync(string url, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized
            && _config.AuthMode == AuthMode.UserCredentials)
        {
            response.Dispose();
            await EnsureAuthenticatedAsync(ct, forceRefresh: true);
            response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        return response;
    }

    // -------------------------------------------------------------------------
    // IMediaServerConnector
    // -------------------------------------------------------------------------

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            if (_config.AuthMode == AuthMode.UserCredentials)
                await EnsureAuthenticatedAsync(cts.Token, forceRefresh: true);

            // GET / returns server info including version
            var doc = await GetXmlAsync(string.Empty, cts.Token);
            var version = doc?.Root?.Attribute("version")?.Value ?? "unknown";
            return ConnectorTestResult.Ok(version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed for connector {ConnectorId}", ConnectorId);
            return ConnectorTestResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<RemoteLibrary>> ListLibrariesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var doc = await GetXmlAsync("library/sections", cancellationToken);
            if (doc?.Root is null) return Array.Empty<RemoteLibrary>();

            return doc.Root.Elements("Directory")
                .Select(d => new RemoteLibrary
                {
                    Id = d.Attribute("key")?.Value ?? string.Empty,
                    Name = d.Attribute("title")?.Value ?? string.Empty,
                    Type = MapSectionType(d.Attribute("type")?.Value)
                })
                .Where(l => !string.IsNullOrEmpty(l.Id) && l.Type != LibraryType.Unknown)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list libraries for connector {ConnectorId}", ConnectorId);
            return Array.Empty<RemoteLibrary>();
        }
    }

    public async Task<IReadOnlyList<MediaItem>> ListItemsAsync(
        string libraryId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sectionType = await GetSectionTypeAsync(libraryId, cancellationToken);

            // Episodes use type=4; music tracks use type=10; other types use default listing
            var typeParam = sectionType switch
            {
                "show"   => "type=4&",
                "artist" => "type=10&",
                _        => string.Empty
            };

            var items = new List<MediaItem>();
            var offset = 0;
            var totalSize = int.MaxValue;

            while (offset < totalSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url = $"library/sections/{libraryId}/all?{typeParam}X-Plex-Container-Start={offset}&X-Plex-Container-Size={PageSize}";

                try
                {
                    var doc = await GetXmlAsync(url, cancellationToken);
                    if (doc?.Root is null) break;

                    var totalAttr = doc.Root.Attribute("totalSize")?.Value;
                    if (totalAttr is not null && int.TryParse(totalAttr, out var total))
                        totalSize = total;

                    List<MediaItem?> mapped;
                    int elementCount;

                    if (sectionType == "photo")
                    {
                        var photos = doc.Root.Elements("Photo").ToList();
                        elementCount = photos.Count;
                        mapped = photos.Select(MapPhotoToItem).ToList();
                    }
                    else if (sectionType == "artist")
                    {
                        var tracks = doc.Root.Elements("Track").ToList();
                        elementCount = tracks.Count;
                        mapped = tracks.Select(MapTrackToItem).ToList();
                    }
                    else
                    {
                        var videos = doc.Root.Elements("Video").ToList();
                        elementCount = videos.Count;
                        mapped = videos.Select(MapVideoToItem).ToList<MediaItem?>();
                    }

                    if (elementCount == 0) break;

                    foreach (var item in mapped)
                        if (item is not null) items.Add(item);

                    offset += elementCount;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error fetching page at offset={Offset} for library {LibraryId}", offset, libraryId);
                    break;
                }
            }

            return items;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list items for library {LibraryId} on connector {ConnectorId}", libraryId, ConnectorId);
            return Array.Empty<MediaItem>();
        }
    }

    public async Task<int> GetItemCountAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sectionType = await GetSectionTypeAsync(libraryId, cancellationToken);
            var url = sectionType switch
            {
                "show"   => $"library/sections/{libraryId}/all?type=4&X-Plex-Container-Start=0&X-Plex-Container-Size=0",
                "artist" => $"library/sections/{libraryId}/all?type=10&X-Plex-Container-Start=0&X-Plex-Container-Size=0",
                _        => $"library/sections/{libraryId}/all?X-Plex-Container-Start=0&X-Plex-Container-Size=0"
            };

            var doc = await GetXmlAsync(url, cancellationToken);
            var totalAttr = doc?.Root?.Attribute("totalSize")?.Value;
            return totalAttr is not null && int.TryParse(totalAttr, out var count) ? count : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get item count for library {LibraryId} on connector {ConnectorId}", libraryId, ConnectorId);
            return 0;
        }
    }

    public async Task<MediaMetadata> GetMetadataAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var doc = await GetXmlAsync($"library/metadata/{itemId}", cancellationToken);
        var root = doc?.Root ?? throw new InvalidOperationException($"Empty response for item {itemId}");

        // Episodes/Movies → Video element; Shows/Seasons → Directory element
        var mediaElement = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName is "Video" or "Track" or "Photo" or "Directory");
        if (mediaElement is null)
            throw new InvalidOperationException($"No media element in metadata response for item {itemId}");

        // Shows and Seasons are Directory elements — map them as container metadata
        if (mediaElement.Name.LocalName == "Directory")
            return MapDirectoryToMetadata(mediaElement);

        return MapVideoToMetadata(mediaElement);
    }

    public async Task<string> GetStreamUrlAsync(string itemId, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var doc = await GetXmlAsync($"library/metadata/{itemId}", cancellationToken);
        var mediaElement = doc?.Root?.Elements()
            .FirstOrDefault(e => e.Name.LocalName is "Video" or "Track" or "Photo");
        var partKey = mediaElement?.Element("Media")?.Element("Part")?.Attribute("key")?.Value;

        if (string.IsNullOrEmpty(partKey))
            throw new InvalidOperationException($"No Part key found in metadata for item {itemId}");

        // partKey is "/library/parts/{id}/file" — build absolute URL with token
        var baseUrl = _config.ServerUrl.TrimEnd('/');
        return $"{baseUrl}{partKey}?X-Plex-Token={_plexToken}";
    }

    public async Task<Stream?> GetArtworkStreamAsync(
        string itemId,
        ArtworkType artworkType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var doc = await GetXmlAsync($"library/metadata/{itemId}", cancellationToken);
            // Video for episodes/movies, Directory for shows/seasons
            var element = doc?.Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName is "Video" or "Track" or "Photo" or "Directory");
            if (element is null) return null;

            // Plex provides thumb (poster) and art (backdrop)
            var artPath = artworkType == ArtworkType.Backdrop
                ? element.Attribute("art")?.Value
                : element.Attribute("thumb")?.Value;

            if (string.IsNullOrEmpty(artPath)) return null;

            // artPath is a server-relative path like "/library/metadata/{id}/thumb/..."
            var relativePath = artPath.TrimStart('/');
            var response = await GetStreamResponseAsync(relativePath, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get artwork {ArtworkType} for item {ItemId}", artworkType, itemId);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Playback reporting — Plex /:/timeline endpoint
    // -------------------------------------------------------------------------

    public async Task ReportPlaybackStartAsync(string itemId, string playSessionId, CancellationToken cancellationToken = default)
    {
        try { await ReportTimelineAsync(itemId, "playing", 0L, playSessionId, cancellationToken); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to report PlaybackStart for item {ItemId}", itemId); }
    }

    public async Task ReportPlaybackProgressAsync(string itemId, string playSessionId, long positionTicks, bool isPaused, CancellationToken cancellationToken = default)
    {
        try
        {
            var state = isPaused ? "paused" : "playing";
            await ReportTimelineAsync(itemId, state, positionTicks / 10_000L, playSessionId, cancellationToken);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to report PlaybackProgress for item {ItemId}", itemId); }
    }

    public async Task ReportPlaybackStoppedAsync(string itemId, string playSessionId, long positionTicks, CancellationToken cancellationToken = default)
    {
        try { await ReportTimelineAsync(itemId, "stopped", positionTicks / 10_000L, playSessionId, cancellationToken); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to report PlaybackStopped for item {ItemId}", itemId); }
    }

    /// <summary>
    /// Appelle GET /:/timeline sur le serveur Plex.
    /// Plex utilise cet endpoint pour mettre à jour le statut de lecture (state),
    /// la position (time en ms) et la progression (viewOffset).
    /// X-Plex-Session-Identifier est envoyé par requête car chaque session a son propre ID.
    /// </summary>
    private async Task ReportTimelineAsync(string itemId, string state, long positionMs, string playSessionId, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);

        var baseUrl = _config.ServerUrl.TrimEnd('/');

        // /:/timeline — met à jour le statut "Now Playing" (visible dans le dashboard Plex)
        var timelineUrl = $"{baseUrl}/:/timeline" +
                          $"?hasMDE=1" +
                          $"&ratingKey={itemId}" +
                          $"&key=/library/metadata/{itemId}" +
                          $"&state={state}" +
                          $"&time={positionMs}" +
                          $"&X-Plex-Token={_plexToken}";

        using var timelineRequest = new HttpRequestMessage(HttpMethod.Get, timelineUrl);
        timelineRequest.Headers.TryAddWithoutValidation("X-Plex-Session-Identifier", playSessionId);
        using var timelineResponse = await _httpClient.SendAsync(timelineRequest, ct);

        var timelineBody = timelineResponse.IsSuccessStatusCode ? string.Empty
                           : await timelineResponse.Content.ReadAsStringAsync(ct);
        Console.Error.WriteLine(
            $"[VirtualLib] Plex timeline state={state} item={itemId} pos={positionMs}ms → {(int)timelineResponse.StatusCode}" +
            (string.IsNullOrEmpty(timelineBody) ? string.Empty : $" body={timelineBody}"));

        // /:/progress — persiste le viewOffset en base (pour "Continuer la lecture")
        // key = ratingKey numérique (PAS /library/metadata/id), state requis
        // Appelé pour paused et stopped uniquement
        if (state != "playing" && positionMs > 0)
        {
            var progressUrl = $"{baseUrl}/:/progress" +
                              $"?key={itemId}" +
                              $"&identifier=com.plexapp.plugins.library" +
                              $"&time={positionMs}" +
                              $"&state={state}" +
                              $"&X-Plex-Token={_plexToken}";

            using var progressRequest = new HttpRequestMessage(HttpMethod.Get, progressUrl);
            using var progressResponse = await _httpClient.SendAsync(progressRequest, ct);

            var progressBody = progressResponse.IsSuccessStatusCode ? string.Empty
                               : await progressResponse.Content.ReadAsStringAsync(ct);
            Console.Error.WriteLine(
                $"[VirtualLib] Plex progress item={itemId} pos={positionMs}ms → {(int)progressResponse.StatusCode}" +
                (string.IsNullOrEmpty(progressBody) ? string.Empty : $" body={progressBody}"));
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the Plex section type string ("movie", "show", "artist") for a given section key.
    /// Checks KnownLibraries first to avoid an extra API call during sync.
    /// </summary>
    private async Task<string> GetSectionTypeAsync(string sectionId, CancellationToken ct)
    {
        // Fast path: look up cached type from known libraries
        var known = _config.KnownLibraries.FirstOrDefault(l => l.Id == sectionId);
        if (known?.Type is not null)
        {
            return known.Type switch
            {
                "TvShows" => "show",
                "Movies"  => "movie",
                "Music"   => "artist",
                "Photos"  => "photo",
                _         => "movie"
            };
        }

        // Slow path: fetch section list from Plex
        try
        {
            var doc = await GetXmlAsync("library/sections", ct);
            var section = doc?.Root?.Elements("Directory")
                .FirstOrDefault(d => d.Attribute("key")?.Value == sectionId);
            return section?.Attribute("type")?.Value ?? "movie";
        }
        catch
        {
            return "movie";
        }
    }

    private MediaItem? MapVideoToItem(XElement video)
    {
        var type = video.Attribute("type")?.Value switch
        {
            "movie"   => (MediaType?)MediaType.Movie,
            "episode" => MediaType.Episode,
            _         => null
        };

        if (type is null)
        {
            _logger.LogWarning("Unknown video type '{Type}' for item {ItemId} — skipping",
                video.Attribute("type")?.Value, video.Attribute("ratingKey")?.Value);
            return null;
        }

        var itemDurationMs = long.TryParse(video.Attribute("duration")?.Value, out var d) ? d : 0L;
        var viewCount      = int.TryParse(video.Attribute("viewCount")?.Value, out var vc) ? vc : 0;
        var viewOffsetMs   = long.TryParse(video.Attribute("viewOffset")?.Value, out var vo) ? vo : 0L;
        var lastViewedAt   = ParseUnixTimestamp(video, "lastViewedAt");

        return new MediaItem
        {
            RemoteId        = video.Attribute("ratingKey")?.Value ?? string.Empty,
            Title           = video.Attribute("title")?.Value ?? string.Empty,
            Type            = type.Value,
            Year            = ParseInt(video, "year"),
            SeriesId        = video.Attribute("grandparentRatingKey")?.Value,
            SeasonId        = video.Attribute("parentRatingKey")?.Value,
            SeriesName      = video.Attribute("grandparentTitle")?.Value,
            SeasonNumber    = ParseInt(video, "parentIndex"),
            EpisodeNumber   = ParseInt(video, "index"),
            ImdbId          = ParseGuid(video, "imdb://"),
            TmdbId          = ParseGuid(video, "tmdb://"),
            TvdbId          = ParseGuid(video, "tvdb://"),
            DateAdded       = ParseUnixTimestamp(video, "addedAt"),
            RuntimeTicks    = itemDurationMs > 0 ? itemDurationMs * 10_000L : null,
            AvailableArtwork = BuildAvailableArtwork(video),
            Technical       = ParseTechnicalInfo(video),
            IsPlayed              = viewCount > 0,
            PlayCount             = viewCount,
            LastPlayedDate        = lastViewedAt,
            PlaybackPositionTicks = viewOffsetMs > 0 ? viewOffsetMs * 10_000L : 0
        };
    }

    private MediaMetadata MapVideoToMetadata(XElement video)
    {
        var type = video.Attribute("type")?.Value switch
        {
            "episode" => MediaType.Episode,
            _         => MediaType.Movie
        };

        var durationMs   = long.TryParse(video.Attribute("duration")?.Value, out var d) ? d : 0L;
        var metaViewCount = int.TryParse(video.Attribute("viewCount")?.Value, out var mvc) ? mvc : 0;
        var metaViewOffMs = long.TryParse(video.Attribute("viewOffset")?.Value, out var mvo) ? mvo : 0L;
        var rating = float.TryParse(
            video.Attribute("rating")?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var r) ? r : (float?)null;

        return new MediaMetadata
        {
            RemoteId         = video.Attribute("ratingKey")?.Value ?? string.Empty,
            Title            = video.Attribute("title")?.Value ?? string.Empty,
            Type             = type,
            Year             = ParseInt(video, "year"),
            SeriesId         = video.Attribute("grandparentRatingKey")?.Value,
            SeasonId         = video.Attribute("parentRatingKey")?.Value,
            SeriesName       = video.Attribute("grandparentTitle")?.Value,
            SeasonNumber     = ParseInt(video, "parentIndex"),
            EpisodeNumber    = ParseInt(video, "index"),
            ImdbId           = ParseGuid(video, "imdb://"),
            TmdbId           = ParseGuid(video, "tmdb://"),
            TvdbId           = ParseGuid(video, "tvdb://"),
            DateAdded        = ParseUnixTimestamp(video, "addedAt"),
            RuntimeTicks     = durationMs > 0 ? durationMs * 10_000L : null,
            AvailableArtwork = BuildAvailableArtwork(video),
            Overview         = video.Attribute("summary")?.Value,
            CommunityRating  = rating,
            RuntimeMinutes   = durationMs > 0 ? (int)(durationMs / 60_000L) : null,
            Genres           = video.Elements("Genre")
                                   .Select(g => g.Attribute("tag")?.Value ?? string.Empty)
                                   .Where(g => !string.IsNullOrEmpty(g))
                                   .ToList(),
            Studios          = video.Attribute("studio")?.Value is { Length: > 0 } studio
                                   ? new List<string> { studio }
                                   : (IReadOnlyList<string>)Array.Empty<string>(),
            Tags             = Array.Empty<string>(),
            OfficialRating   = video.Attribute("contentRating")?.Value,
            Cast             = video.Elements("Role")
                                   .Select(e => new PersonInfo
                                   {
                                       Name = e.Attribute("tag")?.Value ?? string.Empty,
                                       Role = e.Attribute("role")?.Value
                                   })
                                   .Where(p => !string.IsNullOrEmpty(p.Name))
                                   .ToList(),
            Directors        = video.Elements("Director")
                                   .Select(e => e.Attribute("tag")?.Value ?? string.Empty)
                                   .Where(s => !string.IsNullOrEmpty(s))
                                   .ToList(),
            Writers          = video.Elements("Writer")
                                   .Select(e => e.Attribute("tag")?.Value ?? string.Empty)
                                   .Where(s => !string.IsNullOrEmpty(s))
                                   .ToList(),
            Tagline          = video.Attribute("tagline")?.Value,
            TrailerUrl       = null, // Plex does not expose trailer URLs in metadata
            Technical        = ParseTechnicalInfo(video),
            IsPlayed              = metaViewCount > 0,
            PlayCount             = metaViewCount,
            LastPlayedDate        = ParseUnixTimestamp(video, "lastViewedAt"),
            PlaybackPositionTicks = metaViewOffMs > 0 ? metaViewOffMs * 10_000L : 0
        };
    }

    /// <summary>
    /// Maps a Plex Directory element (Show or Season) to MediaMetadata for artwork/NFO generation.
    /// </summary>
    private MediaMetadata MapDirectoryToMetadata(XElement dir)
    {
        var type = dir.Attribute("type")?.Value switch
        {
            "show"   => MediaType.Episode, // treat as series container
            "season" => MediaType.Episode, // treat as season container
            _        => MediaType.Movie
        };

        var rating = float.TryParse(
            dir.Attribute("rating")?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var r) ? r : (float?)null;

        return new MediaMetadata
        {
            RemoteId         = dir.Attribute("ratingKey")?.Value ?? string.Empty,
            Title            = dir.Attribute("title")?.Value ?? string.Empty,
            Type             = type,
            Year             = ParseInt(dir, "year"),
            SeasonNumber     = ParseInt(dir, "index"),
            SeriesName       = dir.Attribute("parentTitle")?.Value,
            Overview         = dir.Attribute("summary")?.Value,
            CommunityRating  = rating,
            OfficialRating   = dir.Attribute("contentRating")?.Value,
            TvdbId           = ParseGuid(dir, "tvdb://"),
            AvailableArtwork = BuildAvailableArtwork(dir),
            Genres           = dir.Elements("Genre")
                                  .Select(g => g.Attribute("tag")?.Value ?? string.Empty)
                                  .Where(g => !string.IsNullOrEmpty(g))
                                  .ToList(),
        };
    }

    /// <summary>
    /// Parses TechnicalInfo from a Plex &lt;Video&gt; element's &lt;Media&gt; child.
    /// Plex bitrate is in kbps → convert to bps. AudioSampleRate from &lt;Stream&gt; if not on &lt;Media&gt;.
    /// </summary>
    private static TechnicalInfo? ParseTechnicalInfo(XElement video)
    {
        var media = video.Element("Media");
        if (media is null) return null;

        // Bitrate on <Media> is in kbps
        int? bitrate = int.TryParse(media.Attribute("bitrate")?.Value, out var br) ? br * 1000 : null;

        // AudioSampleRate: check <Media> first, then <Part><Stream audioSamplingRate="...">
        int? sampleRate = ParseInt(media, "audioSampleRate");
        if (sampleRate is null)
        {
            var part = media.Element("Part");
            sampleRate = part?.Elements("Stream")
                .Where(s => s.Attribute("streamType")?.Value == "2") // 2 = audio
                .Select(s => ParseInt(s, "samplingRate"))
                .FirstOrDefault(v => v.HasValue);
        }

        // File size from <Part size="...">
        long? size = long.TryParse(media.Element("Part")?.Attribute("size")?.Value, out var sz) ? sz : null;

        var result = new TechnicalInfo
        {
            Container       = media.Attribute("container")?.Value,
            Bitrate         = bitrate,
            Width           = ParseInt(media, "width"),
            Height          = ParseInt(media, "height"),
            VideoCodec      = media.Attribute("videoCodec")?.Value,
            AudioCodec      = media.Attribute("audioCodec")?.Value,
            AudioChannels   = ParseInt(media, "audioChannels"),
            AudioSampleRate = sampleRate,
            Size            = size
        };

        // Return null if nothing meaningful was found
        bool hasAny = result.Container is not null || result.VideoCodec is not null
                   || result.AudioCodec is not null || result.Width.HasValue
                   || result.Height.HasValue || result.Bitrate.HasValue;
        return hasAny ? result : null;
    }

    private static string? ParseGuid(XElement video, string prefix) =>
        video.Elements("Guid")
             .Select(g => g.Attribute("id")?.Value)
             .FirstOrDefault(id => id?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
             ?.Substring(prefix.Length);

    private static int? ParseInt(XElement el, string attribute) =>
        int.TryParse(el.Attribute(attribute)?.Value, out var v) ? v : null;

    private static DateTime? ParseUnixTimestamp(XElement el, string attribute) =>
        long.TryParse(el.Attribute(attribute)?.Value, out var ts)
            ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
            : null;

    private static IReadOnlyList<ArtworkType> BuildAvailableArtwork(XElement video)
    {
        var result = new List<ArtworkType>();
        if (!string.IsNullOrEmpty(video.Attribute("thumb")?.Value)) result.Add(ArtworkType.Poster);
        if (!string.IsNullOrEmpty(video.Attribute("art")?.Value))   result.Add(ArtworkType.Backdrop);
        return result;
    }

    public async Task<string> DownloadFileToPathAsync(string itemId, string destPathNoExt, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);

        var doc = await GetXmlAsync($"library/metadata/{itemId}", ct);
        var mediaElement = doc?.Root?.Elements()
            .FirstOrDefault(e => e.Name.LocalName is "Video" or "Track" or "Photo");
        var part = mediaElement?.Element("Media")?.Element("Part");
        var partKey = part?.Attribute("key")?.Value;
        var partFile = part?.Attribute("file")?.Value;

        if (string.IsNullOrEmpty(partKey))
            throw new InvalidOperationException($"No Part key for item {itemId}");

        // Prefer extension from the original filename stored on the Plex server
        var extension = !string.IsNullOrEmpty(partFile) && Path.GetExtension(partFile) is { Length: > 1 } e
            ? e
            : ".epub";

        var destPath = destPathNoExt + extension;
        var relativePath = partKey.TrimStart('/');

        using var response = await GetStreamResponseAsync(relativePath, ct);
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await contentStream.CopyToAsync(fileStream, ct);

        _logger.LogInformation("Downloaded book file for item {ItemId} → '{Path}'", itemId, destPath);
        return destPath;
    }

    private MediaItem? MapPhotoToItem(XElement photo)
    {
        var id = photo.Attribute("ratingKey")?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(id)) return null;

        return new MediaItem
        {
            RemoteId         = id,
            Title            = photo.Attribute("title")?.Value ?? string.Empty,
            Type             = MediaType.Photo,
            Year             = ParseInt(photo, "year"),
            DateAdded        = ParseUnixTimestamp(photo, "addedAt"),
            AvailableArtwork = BuildAvailableArtwork(photo)
        };
    }

    private MediaItem? MapTrackToItem(XElement track)
    {
        var id = track.Attribute("ratingKey")?.Value ?? string.Empty;
        if (string.IsNullOrEmpty(id)) return null;

        return new MediaItem
        {
            RemoteId         = id,
            Title            = track.Attribute("title")?.Value ?? string.Empty,
            Type             = MediaType.Music,
            Year             = ParseInt(track, "year"),
            SeriesName       = track.Attribute("grandparentTitle")?.Value,
            DateAdded        = ParseUnixTimestamp(track, "addedAt"),
            AvailableArtwork = BuildAvailableArtwork(track)
        };
    }

    private static LibraryType MapSectionType(string? type) => type switch
    {
        "movie"  => LibraryType.Movies,
        "show"   => LibraryType.TvShows,
        "artist" => LibraryType.Music,
        "photo"  => LibraryType.Photos,
        _        => LibraryType.Unknown
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _authLock.Dispose();
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
