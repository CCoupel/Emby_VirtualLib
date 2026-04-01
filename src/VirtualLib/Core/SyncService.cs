using Microsoft.Extensions.Logging;
using VirtualLib.Core.Models;

namespace VirtualLib.Core;

/// <summary>
/// Progress information reported during a sync operation.
/// </summary>
public sealed class SyncProgress
{
    public string LibraryName { get; init; } = string.Empty;
    public int Current { get; init; }
    public int Total { get; init; }
    public string CurrentItem { get; init; } = string.Empty;
}

public sealed class LibrarySyncResult
{
    public string LibraryName { get; init; } = string.Empty;
    public int ItemsCreated { get; init; }
    public int ItemsSkipped { get; init; }
    public int ItemsFailed { get; init; }
}

/// <summary>
/// Result of a completed sync operation.
/// </summary>
public sealed class SyncResult
{
    public bool Success { get; init; }
    public string ConnectorName { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public int ItemsCreated { get; init; }
    public int ItemsSkipped { get; init; }
    public int ItemsFailed { get; init; }
    public TimeSpan Duration { get; init; }
    public List<LibrarySyncResult> Libraries { get; init; } = new();

    public static SyncResult Failure(string connectorName, string error, TimeSpan duration) =>
        new() { Success = false, ConnectorName = connectorName, ErrorMessage = error, Duration = duration };

    public static SyncResult Completed(string connectorName, int created, int skipped, int failed, TimeSpan duration, List<LibrarySyncResult> libraries) =>
        new() { Success = true, ConnectorName = connectorName, ItemsCreated = created, ItemsSkipped = skipped, ItemsFailed = failed, Duration = duration, Libraries = libraries };
}

/// <summary>
/// Orchestrates a manual or scheduled sync for a single <see cref="ConnectorConfig"/>.
/// Thread-safe; each call creates its own connector instance.
/// </summary>
public sealed class SyncService
{
    private readonly IConnectorFactory _connectorFactory;
    private readonly StrmGenerator _strmGenerator;
    private readonly NfoGenerator _nfoGenerator;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IConnectorFactory connectorFactory,
        StrmGenerator strmGenerator,
        NfoGenerator nfoGenerator,
        ILogger<SyncService> logger)
    {
        _connectorFactory = connectorFactory;
        _strmGenerator = strmGenerator;
        _nfoGenerator = nfoGenerator;
        _logger = logger;
    }

    /// <summary>
    /// Synchronises one connector: tests the connection, then iterates over each
    /// configured library and generates .strm + .nfo files for every item found.
    /// </summary>
    public async Task<SyncResult> SyncConnectorAsync(
        ConnectorConfig config,
        string virtualLibRoot,
        string proxyBaseUrl,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        int created = 0;
        int skipped = 0;
        int failed = 0;

        _logger.LogInformation(
            "Starting sync for connector '{DisplayName}' ({ConnectorId})",
            config.DisplayName, config.Id);

        using var connector = _connectorFactory.Create(config);

        // --- 1. Test connection ---
        ConnectorTestResult testResult;
        try
        {
            testResult = await connector.TestConnectionAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test threw an exception for connector {ConnectorId}", config.Id);
            return SyncResult.Failure(config.DisplayName, $"Connection test failed: {ex.Message}", DateTime.UtcNow - startTime);
        }

        if (!testResult.Success)
        {
            _logger.LogWarning(
                "Connection test failed for connector {ConnectorId}: {Error}",
                config.Id, testResult.ErrorMessage);
            return SyncResult.Failure(
                config.DisplayName,
                $"Connection test failed: {testResult.ErrorMessage}",
                DateTime.UtcNow - startTime);
        }

        _logger.LogInformation(
            "Connection OK for connector {ConnectorId} — server version {Version}",
            config.Id, testResult.ServerVersion);

        // --- 2. Retrieve the library name mapping for progress reporting ---
        IReadOnlyList<RemoteLibrary> allLibraries = Array.Empty<RemoteLibrary>();
        try
        {
            allLibraries = await connector.ListLibrariesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list libraries for connector {ConnectorId}; will use IDs as names", config.Id);
        }

        var libraryNameMap = allLibraries.ToDictionary(l => l.Id, l => l.Name);

        // --- 3. Sync each configured library ---
        var libraryResults = new List<LibrarySyncResult>();

        foreach (var libraryId in config.LibraryIds)
        {
            ct.ThrowIfCancellationRequested();

            var libraryName = libraryNameMap.TryGetValue(libraryId, out var name)
                ? name
                : libraryId;

            _logger.LogInformation(
                "Syncing library '{LibraryName}' ({LibraryId}) for connector {ConnectorId}",
                libraryName, libraryId, config.Id);

            var libResult = await SyncLibraryItemsAsync(connector, config, libraryId, libraryName, virtualLibRoot, proxyBaseUrl, progress, ct);
            libraryResults.Add(libResult);
            created += libResult.ItemsCreated;
            skipped += libResult.ItemsSkipped;
            failed += libResult.ItemsFailed;
        }

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Sync complete for connector '{DisplayName}': {Created} created, {Skipped} skipped, {Failed} failed in {Duration}",
            config.DisplayName, created, skipped, failed, duration);

        return SyncResult.Completed(config.DisplayName, created, skipped, failed, duration, libraryResults);
    }

    // -----------------------------------------------------------------
    // Public single-library sync
    // -----------------------------------------------------------------

    /// <summary>Synchronises a single library within a connector.</summary>
    public async Task<SyncResult> SyncLibraryAsync(
        ConnectorConfig config,
        string libraryId,
        string virtualLibRoot,
        string proxyBaseUrl,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;

        using var connector = _connectorFactory.Create(config);

        ConnectorTestResult testResult;
        try { testResult = await connector.TestConnectionAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncResult.Failure(config.DisplayName, $"Connection test failed: {ex.Message}", DateTime.UtcNow - startTime);
        }

        if (!testResult.Success)
            return SyncResult.Failure(config.DisplayName, $"Connection test failed: {testResult.ErrorMessage}", DateTime.UtcNow - startTime);

        var libraryName = config.KnownLibraries?.FirstOrDefault(l => l.Id == libraryId)?.Name ?? libraryId;

        var libResult = await SyncLibraryItemsAsync(connector, config, libraryId, libraryName, virtualLibRoot, proxyBaseUrl, progress, ct);

        var duration = DateTime.UtcNow - startTime;
        return SyncResult.Completed(config.DisplayName, libResult.ItemsCreated, libResult.ItemsSkipped, libResult.ItemsFailed, duration, new List<LibrarySyncResult> { libResult });
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

    private async Task<LibrarySyncResult> SyncLibraryItemsAsync(
        IMediaServerConnector connector,
        ConnectorConfig config,
        string libraryId,
        string libraryName,
        string virtualLibRoot,
        string proxyBaseUrl,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        int libCreated = 0, libSkipped = 0, libFailed = 0;

        IReadOnlyList<MediaItem> items;
        try
        {
            items = await connector.ListItemsAsync(libraryId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list items for library {LibraryId}", libraryId);
            return new LibrarySyncResult { LibraryName = libraryName, ItemsFailed = 1 };
        }

        _logger.LogInformation("Found {Count} items in library '{LibraryName}'", items.Count, libraryName);

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            progress?.Report(new SyncProgress { LibraryName = libraryName, Current = i + 1, Total = items.Count, CurrentItem = item.Title });

            try
            {
                // Always regenerate the .strm (cheap — just a URL line).
                // Skip the expensive metadata fetch only when the .nfo already exists.
                var strmPath = _strmGenerator.Generate(item, config.Id, config.DisplayName, libraryName, virtualLibRoot, proxyBaseUrl);
                var nfoDir   = Path.GetDirectoryName(strmPath) ?? Path.Combine(virtualLibRoot, libraryName);

                // LocalScraping: only .strm is needed — Emby handles metadata/images itself
                if (config.MetadataMode == MetadataMode.LocalScraping)
                {
                    libCreated++;
                    continue;
                }

                // RemoteSync: fetch metadata + write NFO + download artwork
                var nfoPath = Path.Combine(nfoDir, _strmGenerator.GetFileName(item) + ".nfo");
                if (File.Exists(nfoPath)) { libSkipped++; continue; }

                MediaMetadata metadata;
                try { metadata = await connector.GetMetadataAsync(item.RemoteId, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get metadata for item {RemoteId}", item.RemoteId);
                    metadata = BuildFallbackMetadata(item);
                }

                _nfoGenerator.Generate(metadata, nfoDir);
                await DownloadArtworkAsync(connector, metadata, nfoDir, ct);
                libCreated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate files for item '{Title}'", item.Title);
                libFailed++;
            }
        }

        return new LibrarySyncResult { LibraryName = libraryName, ItemsCreated = libCreated, ItemsSkipped = libSkipped, ItemsFailed = libFailed };
    }

    private static readonly Dictionary<ArtworkType, string> _artworkFileNames = new()
    {
        { ArtworkType.Poster,   "poster.jpg"   },
        { ArtworkType.Backdrop, "fanart.jpg"   },
        { ArtworkType.Thumb,    "landscape.jpg" },
        { ArtworkType.Logo,     "logo.png"     },
    };

    private async Task DownloadArtworkAsync(
        IMediaServerConnector connector,
        MediaItem item,
        string targetDir,
        CancellationToken ct)
    {
        foreach (var artworkType in item.AvailableArtwork)
        {
            ct.ThrowIfCancellationRequested();

            if (!_artworkFileNames.TryGetValue(artworkType, out var fileName)) continue;

            var destPath = Path.Combine(targetDir, fileName);
            if (File.Exists(destPath)) continue;

            try
            {
                var stream = await connector.GetArtworkStreamAsync(item.RemoteId, artworkType, ct);
                if (stream is null) continue;

                await using (stream)
                {
                    await using var file = File.Create(destPath);
                    await stream.CopyToAsync(file, ct);
                }

                _logger.LogDebug("Downloaded {ArtworkType} for item {RemoteId}", artworkType, item.RemoteId);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to download {ArtworkType} for item {RemoteId} — skipping", artworkType, item.RemoteId);
            }
        }
    }

    private static MediaMetadata BuildFallbackMetadata(MediaItem item) =>
        new()
        {
            RemoteId = item.RemoteId,
            Title = item.Title,
            Type = item.Type,
            Year = item.Year,
            SeriesId = item.SeriesId,
            SeriesName = item.SeriesName,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            ImdbId = item.ImdbId,
            TmdbId = item.TmdbId,
            TvdbId = item.TvdbId,
            DateAdded = item.DateAdded,
            AvailableArtwork = item.AvailableArtwork,
            Genres = Array.Empty<string>(),
            Studios = Array.Empty<string>(),
            Tags = Array.Empty<string>()
        };
}
