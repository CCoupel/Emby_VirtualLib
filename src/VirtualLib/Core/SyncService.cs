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

/// <summary>
/// Result of a completed sync operation.
/// </summary>
public sealed class SyncResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int ItemsCreated { get; init; }
    public int ItemsSkipped { get; init; }
    public int ItemsFailed { get; init; }
    public TimeSpan Duration { get; init; }

    public static SyncResult Failure(string error, TimeSpan duration) =>
        new() { Success = false, ErrorMessage = error, Duration = duration };

    public static SyncResult Completed(int created, int skipped, int failed, TimeSpan duration) =>
        new() { Success = true, ItemsCreated = created, ItemsSkipped = skipped, ItemsFailed = failed, Duration = duration };
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
            return SyncResult.Failure($"Connection test failed: {ex.Message}", DateTime.UtcNow - startTime);
        }

        if (!testResult.Success)
        {
            _logger.LogWarning(
                "Connection test failed for connector {ConnectorId}: {Error}",
                config.Id, testResult.ErrorMessage);
            return SyncResult.Failure(
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
        foreach (var libraryId in config.LibraryIds)
        {
            ct.ThrowIfCancellationRequested();

            var libraryName = libraryNameMap.TryGetValue(libraryId, out var name)
                ? name
                : libraryId;

            _logger.LogInformation(
                "Syncing library '{LibraryName}' ({LibraryId}) for connector {ConnectorId}",
                libraryName, libraryId, config.Id);

            IReadOnlyList<MediaItem> items;
            try
            {
                items = await connector.ListItemsAsync(libraryId, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list items for library {LibraryId}", libraryId);
                failed++;
                continue;
            }

            _logger.LogInformation(
                "Found {Count} items in library '{LibraryName}'",
                items.Count, libraryName);

            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = items[i];

                progress?.Report(new SyncProgress
                {
                    LibraryName = libraryName,
                    Current = i + 1,
                    Total = items.Count,
                    CurrentItem = item.Title
                });

                try
                {
                    // --- 3a. Skip if .strm already exists ---
                    var strmPath = Path.Combine(
                        _strmGenerator.GetDirectoryPath(item, libraryName, virtualLibRoot),
                        _strmGenerator.GetFileName(item) + ".strm");

                    if (File.Exists(strmPath))
                    {
                        skipped++;
                        continue;
                    }

                    // --- 3b. Get full metadata ---
                    MediaMetadata metadata;
                    try
                    {
                        metadata = await connector.GetMetadataAsync(item.RemoteId, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get metadata for item {RemoteId} — using basic info", item.RemoteId);
                        // Fallback: create minimal metadata from the MediaItem
                        metadata = BuildFallbackMetadata(item);
                    }

                    // --- 3c. Generate .strm ---
                    strmPath = _strmGenerator.Generate(
                        item,
                        config.Id,
                        libraryName,
                        virtualLibRoot);

                    // --- 3d. Generate .nfo ---
                    var nfoDir = Path.GetDirectoryName(strmPath)
                                 ?? Path.Combine(virtualLibRoot, libraryName);
                    _nfoGenerator.Generate(metadata, nfoDir);

                    created++;
                    _logger.LogDebug("Generated files for item '{Title}' ({RemoteId})", item.Title, item.RemoteId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate files for item '{Title}' ({RemoteId})", item.Title, item.RemoteId);
                    failed++;
                }
            }
        }

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Sync complete for connector '{DisplayName}': {Created} created, {Skipped} skipped, {Failed} failed in {Duration}",
            config.DisplayName, created, skipped, failed, duration);

        return SyncResult.Completed(created, skipped, failed, duration);
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

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
