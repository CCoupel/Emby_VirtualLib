using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using VirtualLib.Connectors.Internal;
using VirtualLib.Core;
using VirtualLib.Core.Models;

namespace VirtualLib.Connectors;

public sealed class EmbyConnector : IMediaServerConnector
{
    private const int PageSize = 100;
    private const string EmbyTokenHeader = "X-Emby-Token";

    private readonly ConnectorConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmbyConnector> _logger;
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
        _httpClient.DefaultRequestHeaders.Add(EmbyTokenHeader, config.ApiKey);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await _httpClient.GetAsync("emby/System/Info/Public", cts.Token);
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
            var response = await _httpClient.GetAsync("emby/Library/VirtualFolders", cancellationToken);
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
                var url = $"emby/Users/{userId}/Items" +
                          $"?ParentId={libraryId}" +
                          $"&Recursive=true" +
                          $"&IncludeItemTypes=Movie,Episode" +
                          $"&Fields=Overview,Genres,Studios,ProviderIds,DateCreated,Tags" +
                          $"&StartIndex={startIndex}" +
                          $"&Limit={PageSize}";

                var response = await _httpClient.GetAsync(url, cancellationToken);
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

    public async Task<MediaMetadata> GetMetadataAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var url = $"emby/Items/{itemId}?Fields=Overview,Genres,Studios,ProviderIds,People,Tags";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var item = await response.Content.ReadFromJsonAsync<EmbyItem>(cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException($"Empty response for item {itemId}");

        return MapMetadata(item);
    }

    public Task<string> GetStreamUrlAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var baseUrl = _config.ServerUrl.TrimEnd('/');
        var url = $"{baseUrl}/Videos/{itemId}/stream?api_key={_config.ApiKey}&Static=true";
        return Task.FromResult(url);
    }

    public async Task<Stream?> GetArtworkStreamAsync(
        string itemId,
        ArtworkType artworkType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var imageType = MapArtworkType(artworkType);
            var url = $"emby/Items/{itemId}/Images/{imageType}?Quality=90";
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get artwork {ArtworkType} for item {ItemId}", artworkType, itemId);
            return null;
        }
    }

    private async Task<string?> GetUserIdAsync(CancellationToken cancellationToken)
    {
        if (_userId is not null) return _userId;

        try
        {
            var response = await _httpClient.GetAsync("emby/Users/Me", cancellationToken);
            response.EnsureSuccessStatusCode();
            var user = await response.Content.ReadFromJsonAsync<EmbyUser>(cancellationToken: cancellationToken);
            _userId = user?.Id;
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
            OfficialRating = item.OfficialRating
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
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
