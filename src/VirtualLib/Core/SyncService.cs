using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
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
/// Items created for one library that still need metadata injection (phase 2).
/// </summary>
public sealed class LibraryPendingMetadata
{
    public string ConnectorName     { get; init; } = string.Empty;
    public string LibraryName       { get; init; } = string.Empty;
    public string LibraryFolderPath { get; init; } = string.Empty;
    public List<(string StrmPath, MediaItem Item)> Items { get; init; } = new();
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
    private readonly ILibraryManager? _libraryManager;

    public SyncService(
        IConnectorFactory connectorFactory,
        StrmGenerator strmGenerator,
        EpubStubGenerator epubStubGenerator,
        NfoGenerator nfoGenerator,
        ILogger<SyncService> logger,
        ILibraryManager? libraryManager = null)
    {
        _connectorFactory = connectorFactory;
        _strmGenerator = strmGenerator;
        _epubStubGenerator = epubStubGenerator;
        _nfoGenerator = nfoGenerator;
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Synchronises one connector: tests the connection, then iterates over each
    /// configured library and generates .strm + .nfo files for every item found.
    /// </summary>
    public async Task<(SyncResult Result, List<LibraryPendingMetadata> Pending)> SyncConnectorAsync(
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
            return (SyncResult.Failure(config.DisplayName, $"Connection test failed: {ex.Message}", DateTime.UtcNow - startTime), new List<LibraryPendingMetadata>());
        }

        if (!testResult.Success)
        {
            _logger.LogWarning(
                "Connection test failed for connector {ConnectorId}: {Error}",
                config.Id, testResult.ErrorMessage);
            return (SyncResult.Failure(
                config.DisplayName,
                $"Connection test failed: {testResult.ErrorMessage}",
                DateTime.UtcNow - startTime), new List<LibraryPendingMetadata>());
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
        var libraryResults  = new List<LibrarySyncResult>();
        var pendingByLibrary = new List<LibraryPendingMetadata>();

        foreach (var libraryId in config.LibraryIds)
        {
            ct.ThrowIfCancellationRequested();

            var libraryName = libraryNameMap.TryGetValue(libraryId, out var name)
                ? name
                : libraryId;

            _logger.LogInformation(
                "Syncing library '{LibraryName}' ({LibraryId}) for connector {ConnectorId}",
                libraryName, libraryId, config.Id);

            var (libResult, libPending) = await SyncLibraryItemsAsync(connector, config, libraryId, libraryName, virtualLibRoot, proxyBaseUrl, progress, ct);
            libraryResults.Add(libResult);
            created += libResult.ItemsCreated;
            skipped += libResult.ItemsSkipped;
            failed += libResult.ItemsFailed;

            // Compute the folder path for this library (used by caller for targeted scan)
            var libFolderPath = Path.Combine(
                virtualLibRoot,
                StrmGenerator.SanitizeName(config.DisplayName),
                StrmGenerator.SanitizeName(libraryName));

            pendingByLibrary.Add(new LibraryPendingMetadata
            {
                ConnectorName     = config.DisplayName,
                LibraryName       = libraryName,
                LibraryFolderPath = libFolderPath,
                Items             = libPending
            });
        }

        // Metadata push is now handled by the caller (phase 2).

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

        return (SyncResult.Completed(config.DisplayName, created, skipped, failed, duration, libraryResults), pendingByLibrary);
    }

    // -----------------------------------------------------------------
    // Public single-library sync
    // -----------------------------------------------------------------

    /// <summary>Synchronises a single library within a connector.</summary>
    public async Task<(SyncResult Result, List<LibraryPendingMetadata> Pending)> SyncLibraryAsync(
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
            return (SyncResult.Failure(config.DisplayName, $"Connection test failed: {ex.Message}", DateTime.UtcNow - startTime), new List<LibraryPendingMetadata>());
        }

        if (!testResult.Success)
            return (SyncResult.Failure(config.DisplayName, $"Connection test failed: {testResult.ErrorMessage}", DateTime.UtcNow - startTime), new List<LibraryPendingMetadata>());

        var libraryName = config.KnownLibraries?.FirstOrDefault(l => l.Id == libraryId)?.Name ?? libraryId;

        var (libResult, libPending) = await SyncLibraryItemsAsync(connector, config, libraryId, libraryName, virtualLibRoot, proxyBaseUrl, progress, ct);

        var libFolderPath = Path.Combine(
            virtualLibRoot,
            StrmGenerator.SanitizeName(config.DisplayName),
            StrmGenerator.SanitizeName(libraryName));

        var pending = new List<LibraryPendingMetadata>
        {
            new LibraryPendingMetadata
            {
                ConnectorName     = config.DisplayName,
                LibraryName       = libraryName,
                LibraryFolderPath = libFolderPath,
                Items             = libPending
            }
        };

        var duration = DateTime.UtcNow - startTime;
        return (SyncResult.Completed(config.DisplayName, libResult.ItemsCreated, libResult.ItemsSkipped, libResult.ItemsFailed, duration, new List<LibrarySyncResult> { libResult }), pending);
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

    private async Task<(LibrarySyncResult Result, List<(string StrmPath, MediaItem Item)> PendingStrms)> SyncLibraryItemsAsync(
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
        var pendingStrms     = new List<(string StrmPath, MediaItem Item)>();

        IReadOnlyList<MediaItem> items;
        try
        {
            items = await connector.ListItemsAsync(libraryId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list items for library {LibraryId}", libraryId);
            return (new LibrarySyncResult { LibraryName = libraryName, ItemsFailed = 1 }, new List<(string StrmPath, MediaItem Item)>());
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
                    if (config.MetadataMode != MetadataMode.LocalScraping && !processedBookIds.Contains(bookId))
                    {
                        processedBookIds.Add(bookId);
                        try
                        {
                            var bookMeta = await connector.GetMetadataAsync(bookId, ct);

                            // NFO: write on first sync, or always in RemoteSyncFull mode
                            if (config.MetadataMode == MetadataMode.RemoteSyncFull || !File.Exists(bookNfoPath))
                            {
                                var nfoContent = _nfoGenerator.GenerateAudioBookNfo(bookMeta);
                                if (!string.IsNullOrEmpty(nfoContent))
                                    File.WriteAllText(bookNfoPath, nfoContent, new System.Text.UTF8Encoding(false));
                            }

                            // Artwork: always attempt (DownloadArtworkAsync skips files that already exist).
                            // Use the book container's artwork; fall back to the chapter's artwork when the
                            // container has no images (Emby often returns inherited cover art on Audio items).
                            var artworkSource = bookMeta.AvailableArtwork.Count > 0
                                ? (MediaItem)bookMeta
                                : item;
                            await DownloadArtworkAsync(connector, artworkSource, bookDir, ct, audiobook: true);

                            // Push metadata directly to any existing Folder item (bypasses provider pipeline)
                            PushAudioBookFolderMetadata(bookDir, bookMeta, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch metadata for audiobook '{Series}'", item.SeriesName);
                        }
                    }

                    // Download chapter-specific artwork (Primary → {chapter}.jpg alongside the .strm).
                    // Only the Primary image makes sense per-chapter; all other types belong to the book folder.
                    if (item.AvailableArtwork.Contains(ArtworkType.Poster))
                    {
                        var chapterImgPath = Path.ChangeExtension(chapterPath, ".jpg");
                        if (!File.Exists(chapterImgPath))
                        {
                            try
                            {
                                var stream = await connector.GetArtworkStreamAsync(item.RemoteId, ArtworkType.Poster, ct);
                                if (stream is not null)
                                {
                                    await using (stream)
                                    await using (var file = File.Create(chapterImgPath))
                                        await stream.CopyToAsync(file, ct);
                                }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to download chapter artwork for '{Path}'", chapterPath);
                            }
                        }
                    }

                    // Defer chapter-level metadata push until after the library scan creates the items.
                    pendingStrms.Add((chapterPath, item));

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

                // LocalScraping: only .strm is needed — Emby handles metadata/images itself.
                // Queue for polling with list-level item (Technical may be incomplete).
                if (config.MetadataMode == MetadataMode.LocalScraping)
                {
                    if (item.RuntimeTicks.HasValue || item.Technical is not null)
                        pendingStrms.Add((strmPath, item));
                    libCreated++;
                    continue;
                }

                // RemoteSync / RemoteSyncFull: fetch metadata + write NFO + download artwork.
                var nfoPath = Path.Combine(nfoDir, mediaFileName + ".nfo");
                if (config.MetadataMode == MetadataMode.RemoteSync && File.Exists(nfoPath))
                {
                    // NFO already exists — use item from list for polling (best-effort Technical).
                    if (item.RuntimeTicks.HasValue || item.Technical is not null)
                        pendingStrms.Add((strmPath, item));
                    libSkipped++;
                    continue;
                }

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

                // Use metadata (individual item call) — contains full Technical from MediaSources.
                if (metadata.RuntimeTicks.HasValue || metadata.Technical is not null)
                    pendingStrms.Add((strmPath, metadata));
                libCreated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate files for item '{Title}'", item.Title);
                libFailed++;
            }
        }

        return (new LibrarySyncResult { LibraryName = libraryName, ItemsCreated = libCreated, ItemsSkipped = libSkipped, ItemsFailed = libFailed }, pendingStrms);
    }

    /// <summary>
    /// Pushes book-level metadata directly to the Emby Folder item for an audiobook,
    /// bypassing the ILocalMetadataProvider pipeline which Emby does not invoke for
    /// virtual library folders in Audiobooks libraries.
    /// No-op if the item has not been scanned into Emby yet (new first-time sync).
    /// </summary>
    private void PushAudioBookFolderMetadata(string bookDir, MediaMetadata meta, CancellationToken ct)
    {
        if (_libraryManager is null) return;
        try
        {
            var folder = _libraryManager.FindByPath(bookDir, true) as Folder;
            if (folder is null)
            {
                _logger.LogDebug(
                    "VirtualLib: no Folder item at '{Path}' yet — metadata will be applied on next scan via album.nfo",
                    bookDir);
                return;
            }

            folder.Name             = meta.Title;
            folder.Overview         = meta.Overview;
            folder.CommunityRating  = meta.CommunityRating;

            if (meta.Year.HasValue)
                folder.ProductionYear = meta.Year.Value;

            if (meta.Genres.Count > 0)
                folder.Genres = meta.Genres.ToArray();

            if (meta.Tags.Count > 0)
                folder.Tags = meta.Tags.ToArray();

            _libraryManager.UpdateItem(folder, folder.GetParent(), ItemUpdateType.MetadataEdit, new MetadataRefreshOptions((IDirectoryService)null!));
            _logger.LogInformation(
                "VirtualLib: pushed audiobook metadata for '{Path}' — title='{Title}'",
                bookDir, meta.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VirtualLib: failed to push audiobook metadata for '{Path}'", bookDir);
        }
    }

    /// <summary>
    /// Phase-2 metadata injection: polls until each .strm item appears in the Emby DB
    /// (after the per-library targeted scan), then injects metadata directly.
    /// Reports progress via <paramref name="progress"/> as items are resolved.
    /// Runs until all items are resolved or a 5-minute timeout is reached.
    /// </summary>
    public async Task PushMetadataAsync(
        List<(string StrmPath, MediaItem Item)> pending,
        string libraryName,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        if (_libraryManager is null || pending.Count == 0) return;

        var remaining  = new List<(string StrmPath, MediaItem Item)>(pending);
        var deadline   = DateTime.UtcNow.AddMinutes(5);
        int round      = 0;
        int totalItems = pending.Count;
        int totalPushed = 0;

        // Report total immediately (Current=1 = "starting") so the library bar
        // knows the total before the initial scan delay.
        if (totalItems > 0)
            progress?.Report(new SyncProgress { LibraryName = libraryName, Current = 1, Total = totalItems });

        _logger.LogInformation(
            "VirtualLib: starting metadata push for {Count} .strm item(s) in '{Library}'",
            remaining.Count, libraryName);

        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) break;

            // First round: give the scan a few seconds to start indexing.
            await Task.Delay(round == 0 ? 5000 : 2000, ct).ConfigureAwait(false);
            round++;

            var stillPending = new List<(string StrmPath, MediaItem Item)>();
            int pushed = 0;

            foreach (var (strmPath, item) in remaining)
            {
                var baseItem = _libraryManager.FindByPath(strmPath, false);
                if (baseItem is null)
                {
                    stillPending.Add((strmPath, item));
                    continue;
                }

                // RunTimeTicks — injected for every item type (ffprobe is never run on .strm)
                if (item.RuntimeTicks.HasValue)
                    baseItem.RunTimeTicks = item.RuntimeTicks.Value;

                // Technical metadata (size, resolution, codecs…) from the remote MediaSources
                if (item.Technical is { } tech)
                {
                    if (tech.Size.HasValue)
                        baseItem.Size = tech.Size.Value;

                    if (baseItem is Video video)
                    {
                        if (tech.Width.HasValue)         video.Width        = tech.Width.Value;
                        if (tech.Height.HasValue)        video.Height       = tech.Height.Value;
                        if (tech.Bitrate.HasValue)       video.TotalBitrate = tech.Bitrate.Value;
                        if (!string.IsNullOrEmpty(tech.Container))
                            video.Container = tech.Container;
                    }
                }

                // Audiobook chapter — also inject grouping fields
                if (baseItem is Audio audio)
                {
                    if (!string.IsNullOrEmpty(item.SeriesName))
                        audio.Album = item.SeriesName;

                    if (item.AlbumArtists.Count > 0)
                    {
                        audio.AlbumArtists = item.AlbumArtists.ToArray();
                        audio.Artists      = item.AlbumArtists.ToArray();
                    }
                }

                try
                {
                    totalPushed++;
                    progress?.Report(new SyncProgress { LibraryName = libraryName, Current = totalPushed, Total = totalItems });
                    _libraryManager.UpdateItem(baseItem, baseItem.GetParent(), ItemUpdateType.MetadataEdit, new MetadataRefreshOptions((IDirectoryService)null!));
                    pushed++;
                    _logger.LogInformation(
                        "VirtualLib: pushed metadata — '{Path}' type={Type} ticks={Ticks}",
                        strmPath, baseItem.GetType().Name, item.RuntimeTicks);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "VirtualLib: UpdateItem failed for '{Path}' — will retry", strmPath);
                    stillPending.Add((strmPath, item));
                }
            }

            remaining = stillPending;
            _logger.LogInformation(
                "VirtualLib: metadata push round {Round} — pushed={Pushed}, remaining={Remaining}",
                round, pushed, remaining.Count);
        }

        if (remaining.Count > 0)
            _logger.LogWarning(
                "VirtualLib: {Count} item(s) still unresolved after timeout — they will get metadata on next sync",
                remaining.Count);
        else
            _logger.LogInformation("VirtualLib: all pending .strm metadata pushed successfully");
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
        { ArtworkType.Banner,   "banner.jpg"    },
        { ArtworkType.Disc,     "disc.jpg"      },
        { ArtworkType.Art,      "clearart.png"  },
    };

    // Artwork filenames for music/audiobook libraries — Emby expects folder.jpg for album art
    private static readonly Dictionary<ArtworkType, string> _audioArtworkFileNames = new()
    {
        { ArtworkType.Poster,   "folder.jpg"    },
        { ArtworkType.Backdrop, "fanart.jpg"    },
        { ArtworkType.Thumb,    "landscape.jpg" },
        { ArtworkType.Logo,     "logo.png"      },
        { ArtworkType.Banner,   "banner.jpg"    },
        { ArtworkType.Disc,     "disc.jpg"      },
        { ArtworkType.Art,      "clearart.png"  },
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
