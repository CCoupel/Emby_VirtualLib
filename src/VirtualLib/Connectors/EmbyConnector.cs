using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using VirtualLib.Connectors.Internal;
using VirtualLib.Core;
using VirtualLib.Core.Models;
using PersonInfo = VirtualLib.Core.Models.PersonInfo;

namespace VirtualLib.Connectors;

public sealed class EmbyConnector : IMediaServerConnector
{
    private const int PageSize = 100;
    private const string EmbyTokenHeader = "X-Emby-Token";

    private readonly ConnectorConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmbyConnector> _logger;

    // User-credentials session state
    private string? _sessionToken;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private string? _userId;
    private bool _disposed;

    public string ServerType => ServerTypes.Emby;
    public string ConnectorId => _config.Id;
    public string DisplayName => _config.DisplayName;

    public EmbyConnector(
        ConnectorConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger<EmbyConnector> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // For API key mode, set the token header once at construction.
        // For UserCredentials, the header is set lazily after the first auth.
        if (_config.AuthMode == AuthMode.ApiKey)
            _httpClient.DefaultRequestHeaders.Add(EmbyTokenHeader, config.ApiKey);
    }

    // -------------------------------------------------------------------------
    // Authentication (UserCredentials mode)
    // -------------------------------------------------------------------------

    private async Task EnsureAuthenticatedAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (_config.AuthMode != AuthMode.UserCredentials) return;
        if (_sessionToken != null && !forceRefresh) return;

        await _authLock.WaitAsync(ct);
        try
        {
            // Double-check inside the lock
            if (_sessionToken != null && !forceRefresh) return;

            _logger.LogInformation("Authenticating as user '{Username}' on connector {ConnectorId}",
                _config.Username, ConnectorId);

            // AuthenticateByName requires:
            // 1. X-Emby-Authorization header identifying the client (otherwise 400)
            // 2. A body with Content-Length (not chunked) — ServiceStack may fail to read
            //    chunked bodies for this endpoint, resulting in Username=null → 400
            var bodyJson = System.Text.Json.JsonSerializer.Serialize(
                new { Username = _config.Username, Pw = _config.Password });

            var authRequest = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
            authRequest.Headers.TryAddWithoutValidation(
                "X-Emby-Authorization",
                "MediaBrowser Client=\"VirtualLib\", Device=\"VirtualLib Plugin\", DeviceId=\"VirtualLib\", Version=\"1.0.0\"");
            authRequest.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(authRequest, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbyAuthResult>(cancellationToken: ct)
                         ?? throw new InvalidOperationException("Empty auth response");

            _sessionToken = result.AccessToken;
            if (result.User?.Id is not null)
                _userId = result.User.Id;

            // Update the default auth header for subsequent requests
            if (_httpClient.DefaultRequestHeaders.Contains(EmbyTokenHeader))
                _httpClient.DefaultRequestHeaders.Remove(EmbyTokenHeader);
            _httpClient.DefaultRequestHeaders.Add(EmbyTokenHeader, _sessionToken);

            _logger.LogInformation("Authenticated as '{Username}' (userId={UserId}) on {ConnectorId}",
                _config.Username, _userId, ConnectorId);
        }
        finally
        {
            _authLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // HTTP helpers with 401-retry for user credentials
    // -------------------------------------------------------------------------

    private async Task<HttpResponseMessage> GetWithRetryAsync(
        string url,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await _httpClient.GetAsync(url, completion, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized
            && _config.AuthMode == AuthMode.UserCredentials)
        {
            response.Dispose();
            _logger.LogDebug("401 on {Url} — re-authenticating", url);
            await EnsureAuthenticatedAsync(ct, forceRefresh: true);
            response = await _httpClient.GetAsync(url, completion, ct);
        }

        return response;
    }

    private async Task<HttpResponseMessage> PostWithRetryAsync(
        string url,
        object body,
        CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await _httpClient.PostAsJsonAsync(url, body, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized
            && _config.AuthMode == AuthMode.UserCredentials)
        {
            response.Dispose();
            await EnsureAuthenticatedAsync(ct, forceRefresh: true);
            response = await _httpClient.PostAsJsonAsync(url, body, ct);
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

            // For user credentials, force a fresh auth on every test
            if (_config.AuthMode == AuthMode.UserCredentials)
                await EnsureAuthenticatedAsync(cts.Token, forceRefresh: true);

            using var response = await GetWithRetryAsync("System/Info/Public", cts.Token);
            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<EmbySystemInfo>(cancellationToken: cts.Token);
            return ConnectorTestResult.Ok(info?.Version ?? "unknown");
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
            using var response = await GetWithRetryAsync("Library/VirtualFolders", cancellationToken);
            response.EnsureSuccessStatusCode();
            var folders = await response.Content.ReadFromJsonAsync<List<EmbyLibraryFolder>>(cancellationToken: cancellationToken)
                          ?? new List<EmbyLibraryFolder>();

            return folders
                .Select(f => new RemoteLibrary
                {
                    Id = f.ItemId,
                    Name = f.Name,
                    Type = MapCollectionType(f.CollectionType)
                })
                .Where(l => l.Type != LibraryType.Unknown)
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
        var userId = await GetUserIdAsync(cancellationToken);
        if (userId is null)
        {
            _logger.LogError("Cannot list items: failed to retrieve userId for connector {ConnectorId}", ConnectorId);
            return Array.Empty<MediaItem>();
        }

        var items = new List<MediaItem>();
        var startIndex = 0;
        var totalCount = int.MaxValue;

        while (startIndex < totalCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var url = $"Users/{userId}/Items" +
                          $"?ParentId={libraryId}" +
                          $"&Recursive=true" +
                          $"&IncludeItemTypes=Movie,Episode" +
                          $"&Fields=Overview,Genres,Studios,ProviderIds,DateCreated,Tags" +
                          $"&StartIndex={startIndex}" +
                          $"&Limit={PageSize}";

                using var response = await GetWithRetryAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var page = await response.Content.ReadFromJsonAsync<EmbyItemsResponse>(cancellationToken: cancellationToken);
                if (page is null) break;

                totalCount = page.TotalRecordCount;
                if (page.Items.Count == 0) break;

                foreach (var embyItem in page.Items)
                {
                    var mapped = MapItem(embyItem);
                    if (mapped is not null)
                        items.Add(mapped);
                }

                startIndex += page.Items.Count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching page at StartIndex={StartIndex} for library {LibraryId}", startIndex, libraryId);
                break;
            }
        }

        return items;
    }

    public async Task<int> GetItemCountAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = await GetUserIdAsync(cancellationToken);
            if (userId is null) return 0;

            var url = $"Users/{userId}/Items" +
                      $"?ParentId={libraryId}" +
                      $"&Recursive=true" +
                      $"&IncludeItemTypes=Movie,Episode" +
                      $"&Limit=0";

            using var response = await GetWithRetryAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<EmbyItemsResponse>(cancellationToken: cancellationToken);
            return page?.TotalRecordCount ?? 0;
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
        var url = $"Items/{itemId}?Fields=Overview,Genres,Studios,ProviderIds,People,Tags,RemoteTrailers,Taglines";
        using var response = await GetWithRetryAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var item = await response.Content.ReadFromJsonAsync<EmbyItem>(cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException($"Empty response for item {itemId}");

        return MapMetadata(item);
    }

    public async Task<string> GetStreamUrlAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var baseUrl = _config.ServerUrl.TrimEnd('/');
        string token;

        if (_config.AuthMode == AuthMode.UserCredentials)
        {
            await EnsureAuthenticatedAsync(cancellationToken);
            token = _sessionToken!;
        }
        else
        {
            token = _config.ApiKey;
        }

        return $"{baseUrl}/Videos/{itemId}/stream?api_key={token}&Static=true";
    }

    public async Task<Stream?> GetArtworkStreamAsync(
        string itemId,
        ArtworkType artworkType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var imageType = MapArtworkType(artworkType);
            var url = $"Items/{itemId}/Images/{imageType}?Quality=90";

            // Response NOT disposed here — caller owns the returned stream
            var response = await GetWithRetryAsync(url, cancellationToken, HttpCompletionOption.ResponseHeadersRead);

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

    public async Task ReportPlaybackStartAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (_config.AuthMode != AuthMode.UserCredentials) return;

        try
        {
            var userId = await GetUserIdAsync(cancellationToken);
            var body = new
            {
                ItemId = itemId,
                MediaSourceId = itemId,
                UserId = userId,
                CanSeek = true,
                QueueableMediaTypes = new[] { "Video" }
            };
            using var response = await PostWithRetryAsync("Sessions/Playing", body, cancellationToken);
            _logger.LogDebug("Reported PlaybackStart for item={ItemId} userId={UserId}", itemId, userId);
        }
        catch (Exception ex)
        {
            // Best-effort — never break the stream because of a reporting failure
            _logger.LogDebug(ex, "Failed to report PlaybackStart for item {ItemId}", itemId);
        }
    }

    public async Task ReportPlaybackStoppedAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (_config.AuthMode != AuthMode.UserCredentials) return;

        try
        {
            var userId = await GetUserIdAsync(cancellationToken);
            var body = new
            {
                ItemId = itemId,
                MediaSourceId = itemId,
                UserId = userId
            };
            using var response = await PostWithRetryAsync("Sessions/Playing/Stopped", body, cancellationToken);
            _logger.LogDebug("Reported PlaybackStopped for item={ItemId} userId={UserId}", itemId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to report PlaybackStopped for item {ItemId}", itemId);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<string?> GetUserIdAsync(CancellationToken cancellationToken)
    {
        if (_userId is not null) return _userId;

        // UserCredentials: authenticate to get userId from the auth response.
        // Never call /Users/Me — some Emby versions route it to /Users/{id},
        // fail to parse "Me" as a Guid, and return 500.
        if (_config.AuthMode == AuthMode.UserCredentials)
        {
            await EnsureAuthenticatedAsync(cancellationToken);
            if (_userId is not null) return _userId;
        }

        // API key mode: fetch the first admin user
        try
        {
            using var response = await GetWithRetryAsync("Users?IsAdministrator=true&Limit=1", cancellationToken);
            response.EnsureSuccessStatusCode();
            var users = await response.Content.ReadFromJsonAsync<List<EmbyUser>>(cancellationToken: cancellationToken);
            _userId = users?.FirstOrDefault()?.Id;
            return _userId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get userId for connector {ConnectorId}", ConnectorId);
            return null;
        }
    }

    private MediaItem? MapItem(EmbyItem item)
    {
        var type = item.Type switch
        {
            "Movie" => MediaType.Movie,
            "Episode" => MediaType.Episode,
            _ => (MediaType?)null
        };

        if (type is null)
        {
            _logger.LogWarning("Unknown item type '{Type}' for item {ItemId} — skipping", item.Type, item.Id);
            return null;
        }

        return new MediaItem
        {
            RemoteId = item.Id,
            Title = item.Name,
            Type = type.Value,
            Year = item.ProductionYear,
            SeriesId = item.SeriesId,
            SeriesName = item.SeriesName,
            SeasonNumber = item.ParentIndexNumber,
            EpisodeNumber = item.IndexNumber,
            ImdbId = item.ProviderIds?.GetValueOrDefault("Imdb"),
            TmdbId = item.ProviderIds?.GetValueOrDefault("Tmdb"),
            TvdbId = item.ProviderIds?.GetValueOrDefault("Tvdb"),
            DateAdded = item.DateCreated,
            AvailableArtwork = GetAvailableArtwork(item)
        };
    }

    private MediaMetadata MapMetadata(EmbyItem item)
    {
        var type = item.Type switch
        {
            "Movie" => MediaType.Movie,
            "Episode" => MediaType.Episode,
            _ => MediaType.Movie
        };

        return new MediaMetadata
        {
            RemoteId = item.Id,
            Title = item.Name,
            Type = type,
            Year = item.ProductionYear,
            SeriesId = item.SeriesId,
            SeriesName = item.SeriesName,
            SeasonNumber = item.ParentIndexNumber,
            EpisodeNumber = item.IndexNumber,
            ImdbId = item.ProviderIds?.GetValueOrDefault("Imdb"),
            TmdbId = item.ProviderIds?.GetValueOrDefault("Tmdb"),
            TvdbId = item.ProviderIds?.GetValueOrDefault("Tvdb"),
            DateAdded = item.DateCreated,
            AvailableArtwork = GetAvailableArtwork(item),
            Overview = item.Overview,
            CommunityRating = item.CommunityRating,
            RuntimeMinutes = item.RunTimeTicks.HasValue
                ? (int)(item.RunTimeTicks.Value / 600_000_000L)
                : null,
            Genres = item.Genres?.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Studios = item.Studios?.Select(s => s.Name).ToList().AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Tags = item.Tags?.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            OfficialRating = item.OfficialRating,
            Cast = item.People?
                .Where(p => string.Equals(p.Type, "Actor", StringComparison.OrdinalIgnoreCase))
                .Select(p => new PersonInfo { Name = p.Name, Role = p.Role })
                .ToList() ?? (IReadOnlyList<PersonInfo>)Array.Empty<PersonInfo>(),
            Directors = item.People?
                .Where(p => string.Equals(p.Type, "Director", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Writers = item.People?
                .Where(p => string.Equals(p.Type, "Writer", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Tagline = item.Taglines?.FirstOrDefault(),
            TrailerUrl = item.RemoteTrailers?.FirstOrDefault()?.Url
        };
    }

    private static IReadOnlyList<ArtworkType> GetAvailableArtwork(EmbyItem item)
    {
        var result = new List<ArtworkType>();
        if (item.ImageTags?.ContainsKey("Primary") == true) result.Add(ArtworkType.Poster);
        if (item.BackdropImageTags?.Count > 0) result.Add(ArtworkType.Backdrop);
        if (item.ImageTags?.ContainsKey("Thumb") == true) result.Add(ArtworkType.Thumb);
        if (item.ImageTags?.ContainsKey("Logo") == true) result.Add(ArtworkType.Logo);
        return result;
    }

    private static LibraryType MapCollectionType(string? collectionType) => collectionType switch
    {
        "movies" => LibraryType.Movies,
        "tvshows" => LibraryType.TvShows,
        "music" => LibraryType.Music,
        "mixed" => LibraryType.Mixed,
        _ => LibraryType.Unknown
    };

    private static string MapArtworkType(ArtworkType type) => type switch
    {
        ArtworkType.Poster => "Primary",
        ArtworkType.Backdrop => "Backdrop",
        ArtworkType.Thumb => "Thumb",
        ArtworkType.Logo => "Logo",
        _ => "Primary"
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
