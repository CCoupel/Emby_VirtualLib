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
    private readonly EpubStubGenerator _epubStubGenerator;
    private readonly NfoGenerator _nfoGenerator;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        IConnectorFactory connectorFactory,
        StrmGenerator strmGenerator,
        EpubStubGenerator epubStubGenerator,
        NfoGenerator nfoGenerator,
        ILogger<SyncService> logger)
    {
        _connectorFactory = connectorFactory;
        _strmGenerator = strmGenerator;
        _epubStubGenerator = epubStubGenerator;
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

        // --- 2b. Merge newly discovered libraries into KnownLibraries ---
        if (allLibraries.Count > 0)
        {
            var existingIds = new HashSet<string>(config.KnownLibraries?.Select(l => l.Id) ?? Enumerable.Empty<string>());
            config.KnownLibraries ??= new List<KnownLibrary>();

            foreach (var lib in allLibraries)
            {
                if (!existingIds.Contains(lib.Id))
                {
                    config.KnownLibraries.Add(new KnownLibrary
                    {
                        Id   = lib.Id,
                        Name = lib.Name,
                        Type = lib.Type.ToString(),
                        RemoteItemCount = -1
                    });
                    _logger.LogInformation(
                        "Connector {ConnectorId}: new library discovered — '{Name}' ({Id})",
                        config.Id, lib.Name, lib.Id);
                }
            }
        }

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

        // Update remote item counts for libraries that were NOT synced (unchecked)
        // Synced libraries already had their count set in SyncLibraryItemsAsync.
        var syncedIds = new HashSet<string>(config.LibraryIds);
        foreach (var knownLib in config.KnownLibraries ?? Enumerable.Empty<KnownLibrary>())
        {
            if (syncedIds.Contains(knownLib.Id)) continue;
            ct.ThrowIfCancellationRequested();
            try { knownLib.RemoteItemCount = await connector.GetItemCountAsync(knownLib.Id, ct); }
            catch { /* best-effort */ }
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
        var processedBookIds = new HashSet<string>(); // avoid re-fetching book metadata per chapter

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

        // Update the cached remote item count so the config page reflects reality after sync
        var known = config.KnownLibraries?.FirstOrDefault(l => l.Id == libraryId);
        if (known != null) known.RemoteItemCount = items.Count;

        _logger.LogInformation("Found {Count} items in library '{LibraryName}'", items.Count, libraryName);

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];

            progress?.Report(new SyncProgress { LibraryName = libraryName, Current = i + 1, Total = items.Count, CurrentItem = item.Title });

            try
            {
                // ---- Audiobook chapters ------------------------------------------------
                // Chapters (AudioBook type with SeriesName set): generate one STRM per chapter
                // organised under the book folder, and fetch book-level NFO/artwork once.
                if (item.Type == MediaType.AudioBook && !string.IsNullOrEmpty(item.SeriesName))
                {
                    var chapterPath  = _strmGenerator.Generate(item, config.Id, config.DisplayName, libraryId, libraryName, virtualLibRoot, proxyBaseUrl);
                    var bookDir      = Path.GetDirectoryName(chapterPath)!;
                    var bookNfoPath  = Path.Combine(bookDir, "album.nfo");
                    var bookId       = item.SeriesId ?? item.RemoteId;

                    // Fetch book-level metadata + artwork only once per book
                    if (config.MetadataMode != MetadataMode.LocalScraping
                        && !processedBookIds.Contains(bookId)
                        && (config.MetadataMode == MetadataMode.RemoteSyncFull || !File.Exists(bookNfoPath)))
                    {
                        processedBookIds.Add(bookId);
                        try
                        {
                            var bookMeta = await connector.GetMetadataAsync(bookId, ct);
                            // album.nfo — standard Emby Music/AudioBook scanner format
                            var nfoContent = _nfoGenerator.GenerateAudioBookNfo(bookMeta);
                            if (!string.IsNullOrEmpty(nfoContent))
                                File.WriteAllText(bookNfoPath, nfoContent, new System.Text.UTF8Encoding(false));
                            await DownloadArtworkAsync(connector, bookMeta, bookDir, ct, audiobook: true);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch metadata for audiobook '{Series}'", item.SeriesName);
                        }
                    }
                    else
                    {
                        processedBookIds.Add(bookId);
                    }

                    libCreated++;
                    continue;
                }

                // ---- Books (full ebook download) ---------------------------------------
                // Emby's BookResolver ignores .strm files — download the real ebook file.
                // Fall back to an epub stub if the download fails so the item stays visible.
                if (item.Type is MediaType.Book)
                {
                    var bookDir      = EpubStubGenerator.GetDirectoryPath(item, config.DisplayName, libraryName, virtualLibRoot);
                    var bookBaseName = _epubStubGenerator.GetFileName(item);
                    var bookPathNoExt = Path.Combine(bookDir, bookBaseName);
                    var bookNfoPath  = bookPathNoExt + ".nfo";

                    // Skip when both a real book file and NFO already exist (RemoteSync)
                    if (config.MetadataMode == MetadataMode.RemoteSync
                        && !BookFileNeedsDownload(bookPathNoExt)
                        && File.Exists(bookNfoPath))
                    {
                        libSkipped++;
                        continue;
                    }

                    Directory.CreateDirectory(bookDir);

                    // Download real file only when absent or when the existing file is a tiny stub
                    if (BookFileNeedsDownload(bookPathNoExt))
                    {
                        DeleteExistingBookFiles(bookPathNoExt); // remove any previous stub
                        try
                        {
                            await connector.DownloadFileToPathAsync(item.RemoteId, bookPathNoExt, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to download book '{Title}' — creating epub stub as fallback", item.Title);
                            _epubStubGenerator.Generate(item, config.Id, config.DisplayName, libraryId, libraryName, virtualLibRoot, proxyBaseUrl);
                        }
                    }

                    if (config.MetadataMode != MetadataMode.LocalScraping
                        && (config.MetadataMode == MetadataMode.RemoteSyncFull || !File.Exists(bookNfoPath)))
                    {
                        try
                        {
                            var bookMeta = await connector.GetMetadataAsync(item.RemoteId, ct);
                            _nfoGenerator.Generate(bookMeta, bookDir);
                            await DownloadArtworkAsync(connector, bookMeta, bookDir, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch metadata for book '{Title}'", item.Title);
                        }
                    }

                    libCreated++;
                    continue;
                }
                // ---- All other media types -------------------------------------------

                // Always regenerate the .strm (cheap — just a URL line).
                var strmPath   = _strmGenerator.Generate(item, config.Id, config.DisplayName, libraryId, libraryName, virtualLibRoot, proxyBaseUrl);
                var nfoDir     = Path.GetDirectoryName(strmPath) ?? Path.Combine(virtualLibRoot, libraryName);
                var mediaFileName = _strmGenerator.GetFileName(item);

                // LocalScraping: only .strm is needed — Emby handles metadata/images itself
                if (config.MetadataMode == MetadataMode.LocalScraping)
                {
                    libCreated++;
                    continue;
                }

                // RemoteSync / RemoteSyncFull: fetch metadata + write NFO + download artwork
                var nfoPath = Path.Combine(nfoDir, mediaFileName + ".nfo");
                if (config.MetadataMode == MetadataMode.RemoteSync && File.Exists(nfoPath)) { libSkipped++; continue; }

                MediaMetadata metadata;
                try { metadata = await connector.GetMetadataAsync(item.RemoteId, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get metadata for item {RemoteId} '{Title}' — will retry on next sync", item.RemoteId, item.Title);
                    libFailed++;
                    continue; // Don't write fallback NFO — let next sync retry
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

    private static string SanitizeFileName(string name) => StrmGenerator.SanitizeName(name);

    // Returns true when no real book file exists yet, or only a tiny stub is present (<4 KB)
    private static bool BookFileNeedsDownload(string pathNoExt)
    {
        var extensions = new[] { ".epub", ".mobi", ".pdf", ".azw3", ".cbz", ".cbr", ".fb2" };
        foreach (var ext in extensions)
        {
            var path = pathNoExt + ext;
            if (File.Exists(path))
                return new FileInfo(path).Length < 4096;
        }
        return true;
    }

    private static void DeleteExistingBookFiles(string pathNoExt)
    {
        var extensions = new[] { ".epub", ".mobi", ".pdf", ".azw3", ".cbz", ".cbr", ".fb2" };
        foreach (var ext in extensions)
        {
            var path = pathNoExt + ext;
            if (File.Exists(path)) try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    // Artwork filenames for video libraries (movies, TV shows)
    private static readonly Dictionary<ArtworkType, string> _videoArtworkFileNames = new()
    {
        { ArtworkType.Poster,   "poster.jpg"    },
        { ArtworkType.Backdrop, "fanart.jpg"    },
        { ArtworkType.Thumb,    "landscape.jpg" },
        { ArtworkType.Logo,     "logo.png"      },
    };

    // Artwork filenames for music/audiobook libraries — Emby expects folder.jpg for album art
    private static readonly Dictionary<ArtworkType, string> _audioArtworkFileNames = new()
    {
        { ArtworkType.Poster,   "folder.jpg"   },
        { ArtworkType.Backdrop, "fanart.jpg"   },
        { ArtworkType.Thumb,    "landscape.jpg" },
        { ArtworkType.Logo,     "logo.png"     },
    };

    private async Task DownloadArtworkAsync(
        IMediaServerConnector connector,
        MediaItem item,
        string targetDir,
        CancellationToken ct,
        bool audiobook = false)
    {
        var artworkFileNames = audiobook ? _audioArtworkFileNames : _videoArtworkFileNames;

        foreach (var artworkType in item.AvailableArtwork)
        {
            ct.ThrowIfCancellationRequested();

            if (!artworkFileNames.TryGetValue(artworkType, out var fileName)) continue;

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

}
