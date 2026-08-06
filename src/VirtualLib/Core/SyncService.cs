using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using EmbyMediaStream        = MediaBrowser.Model.Entities.MediaStream;
using EmbyMediaStreamType    = MediaBrowser.Model.Entities.MediaStreamType;
using EmbyUserDataSaveReason = MediaBrowser.Model.Entities.UserDataSaveReason;
using Microsoft.Extensions.Logging;
using VirtualLib.Core.Cleanup;
using VirtualLib.Core.Filtering;
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
    /// <summary>
    /// Raw remote item count before filtering (#44 D9) — 0 when not meaningful for this report
    /// (e.g. Phase 2 metadata push, which has no filtered/unfiltered distinction). Equal to
    /// <see cref="Total"/> when no media filter is active on the connector, by construction.
    /// </summary>
    public int RemoteTotal { get; init; }
    public string CurrentItem { get; init; } = string.Empty;
}

public sealed class LibrarySyncResult
{
    public string LibraryName { get; init; } = string.Empty;
    public int ItemsCreated { get; init; }
    public int ItemsSkipped { get; init; }
    public int ItemsFailed { get; init; }
    /// <summary>Items écartés par les règles de filtrage (#44). 0 si aucune règle active.</summary>
    public int ItemsFiltered { get; init; }
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
    private readonly IItemRepository? _itemRepository;
    private readonly IUserDataManager? _userDataManager;
    private readonly IUserManager? _userManager;
    private readonly ILibraryCleanupService? _libraryCleanupService;

    public SyncService(
        IConnectorFactory connectorFactory,
        StrmGenerator strmGenerator,
        EpubStubGenerator epubStubGenerator,
        NfoGenerator nfoGenerator,
        ILogger<SyncService> logger,
        ILibraryManager? libraryManager = null,
        IItemRepository? itemRepository = null,
        IUserDataManager? userDataManager = null,
        IUserManager? userManager = null,
        ILibraryCleanupService? libraryCleanupService = null)
    {
        _connectorFactory = connectorFactory;
        _strmGenerator = strmGenerator;
        _epubStubGenerator = epubStubGenerator;
        _nfoGenerator = nfoGenerator;
        _logger = logger;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryCleanupService = libraryCleanupService;
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
        CancellationToken ct,
        bool orphanCleanupEnabled = false)
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
        var libraryTypeMap = allLibraries.ToDictionary(l => l.Id, l => l.Type.ToString());

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
            var libraryType = libraryTypeMap.TryGetValue(libraryId, out var ltype)
                ? ltype
                : (config.KnownLibraries?.FirstOrDefault(l => l.Id == libraryId)?.Type ?? string.Empty);

            _logger.LogInformation(
                "Syncing library '{LibraryName}' ({LibraryId}) for connector {ConnectorId}",
                libraryName, libraryId, config.Id);

            var (libResult, libPending) = await SyncLibraryItemsAsync(connector, config, libraryId, libraryName, libraryType, virtualLibRoot, proxyBaseUrl, progress, ct, orphanCleanupEnabled);
            libraryResults.Add(libResult);
            created += libResult.ItemsCreated;
            skipped += libResult.ItemsSkipped;
            failed += libResult.ItemsFailed;

            // Compute the folder path for this library (used by caller for targeted scan)
            var libFolderPath = GetLibraryFolderPath(virtualLibRoot, config, libraryName, libraryType);

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
        CancellationToken ct,
        bool orphanCleanupEnabled = false)
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

        var knownLib    = config.KnownLibraries?.FirstOrDefault(l => l.Id == libraryId);
        var libraryName = knownLib?.Name ?? libraryId;
        var libraryType = knownLib?.Type ?? string.Empty;

        var (libResult, libPending) = await SyncLibraryItemsAsync(connector, config, libraryId, libraryName, libraryType, virtualLibRoot, proxyBaseUrl, progress, ct, orphanCleanupEnabled);

        var libFolderPath = GetLibraryFolderPath(virtualLibRoot, config, libraryName, libraryType);

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

    private static string GetLibraryFolderPath(string virtualLibRoot, ConnectorConfig config, string libraryName, string libraryType)
    {
        var safeConnector = StrmGenerator.SanitizeName(config.DisplayName);
        var safeLibrary   = StrmGenerator.SanitizeName(libraryName);
        // Physical path is always virtualLibRoot/ConnectorName/LibraryName/ regardless of organization mode.
        return Path.Combine(virtualLibRoot, safeConnector, safeLibrary);
    }

    private async Task<(LibrarySyncResult Result, List<(string StrmPath, MediaItem Item)> PendingStrms)> SyncLibraryItemsAsync(
        IMediaServerConnector connector,
        ConnectorConfig config,
        string libraryId,
        string libraryName,
        string libraryType,
        string virtualLibRoot,
        string proxyBaseUrl,
        IProgress<SyncProgress>? progress,
        CancellationToken ct,
        bool orphanCleanupEnabled = false)
    {
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

        // Raw count straight from ListItemsAsync, captured before filtering — guard rail #3 (D11,
        // CA11) for the cleanup brick below: a remote listing that returned nothing must never be
        // interpreted as "everything is orphaned".
        var rawItemCount = items.Count;

        // Update the cached remote item count so the config page reflects reality after sync.
        // Intentionally the UNFILTERED total (D9) — it reflects the remote source, not the sync result.
        var known = config.KnownLibraries?.FirstOrDefault(l => l.Id == libraryId);
        if (known != null) known.RemoteItemCount = items.Count;

        _logger.LogInformation("Found {Count} items in library '{LibraryName}'", items.Count, libraryName);

        // --- Media filtering (additive, #44) ---
        // Court-circuit total si aucune règle active sur ce connecteur (CA1) : `items` n'est pas
        // ré-attribué et le comportement antérieur au lot est préservé à l'identique.
        // `config.MediaFilter` peut être null pour un connecteur désérialisé depuis un XML de
        // configuration antérieur à #44 — `?.` défensif, urgence régression prod.
        int itemsFiltered = 0;
        if (config.MediaFilter?.IsActive == true)
        {
            var (filteredItems, rejectedCount) = await ApplyMediaFilterAsync(connector, config, items, libraryName, ct);
            items = filteredItems;
            itemsFiltered = rejectedCount;
        }

        // Thread-safe accumulators — items are processed in parallel
        int libCreated = 0, libSkipped = 0, libFailed = 0, done = 0;
        var processedBookIds   = new ConcurrentDictionary<string, bool>();
        var processedSeriesIds = new ConcurrentDictionary<string, bool>();
        var processedSeasonIds = new ConcurrentDictionary<string, bool>();
        var pendingStrms       = new ConcurrentBag<(string StrmPath, MediaItem Item)>();
        // Sidecar files (.nfo, artwork, album.nfo…) expected to exist after this sync — fed to
        // the orphan cleanup brick below (#11/#44). Populated whether the file was newly written
        // or already present: "expected" means "should exist", not "written this run".
        var expectedSidecars   = new ConcurrentBag<string>();
        int total = items.Count;

        // Max 8 concurrent metadata/artwork requests — avoids overwhelming the remote server
        // while still providing a large speedup over fully sequential processing.
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (item, itemCt) =>
            {
                try
                {
                    // ---- Audiobook chapters ------------------------------------------------
                    if (item.Type == MediaType.AudioBook && !string.IsNullOrEmpty(item.SeriesName))
                    {
                        var chapterPath = _strmGenerator.Generate(item, config.Id, config.DisplayName, libraryId, libraryName, virtualLibRoot, proxyBaseUrl, config.LibraryOrganization, libraryType);
                        var bookDir     = Path.GetDirectoryName(chapterPath)!;
                        var bookNfoPath = Path.Combine(bookDir, "album.nfo");
                        var bookId      = item.SeriesId ?? item.RemoteId;
                        expectedSidecars.Add(bookNfoPath);

                        // TryAdd returns true only for the first task that encounters this bookId
                        if (config.MetadataMode != MetadataMode.LocalScraping && processedBookIds.TryAdd(bookId, true))
                        {
                            try
                            {
                                var bookMeta = await connector.GetMetadataAsync(bookId, itemCt);

                                if (config.MetadataMode == MetadataMode.RemoteSyncFull || !File.Exists(bookNfoPath))
                                {
                                    var nfoContent = _nfoGenerator.GenerateAudioBookNfo(bookMeta);
                                    if (!string.IsNullOrEmpty(nfoContent))
                                        File.WriteAllText(bookNfoPath, nfoContent, new System.Text.UTF8Encoding(false));
                                }

                                var artworkSource = bookMeta.AvailableArtwork.Count > 0
                                    ? (MediaItem)bookMeta
                                    : item;
                                await DownloadArtworkAsync(connector, artworkSource, bookDir, itemCt, audiobook: true, onExpectedPath: expectedSidecars.Add);

                                PushAudioBookFolderMetadata(bookDir, bookMeta, itemCt);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to fetch metadata for audiobook '{Series}'", item.SeriesName);
                            }
                        }

                        if (item.AvailableArtwork.Contains(ArtworkType.Poster))
                        {
                            var chapterImgPath = Path.ChangeExtension(chapterPath, ".jpg");
                            expectedSidecars.Add(chapterImgPath);
                            if (!File.Exists(chapterImgPath))
                            {
                                try
                                {
                                    var stream = await connector.GetArtworkStreamAsync(item.RemoteId, ArtworkType.Poster, itemCt);
                                    if (stream is not null)
                                    {
                                        await using (stream)
                                        await using (var file = File.Create(chapterImgPath))
                                            await stream.CopyToAsync(file, itemCt);
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Failed to download chapter artwork for '{Path}'", chapterPath);
                                }
                            }
                        }

                        pendingStrms.Add((chapterPath, item));
                        Interlocked.Increment(ref libCreated);
                        return;
                    }

                    // ---- Books (full ebook download) ---------------------------------------
                    if (item.Type is MediaType.Book)
                    {
                        var bookDir       = EpubStubGenerator.GetDirectoryPath(item, config.DisplayName, libraryName, virtualLibRoot, config.LibraryOrganization, libraryType);
                        var bookBaseName  = _epubStubGenerator.GetFileName(item);
                        var bookPathNoExt = Path.Combine(bookDir, bookBaseName);
                        var bookNfoPath   = bookPathNoExt + ".nfo";
                        expectedSidecars.Add(bookNfoPath);

                        if (config.MetadataMode == MetadataMode.RemoteSync
                            && !BookFileNeedsDownload(bookPathNoExt)
                            && File.Exists(bookNfoPath))
                        {
                            var existingBookPath = FindExistingBookFile(bookPathNoExt);
                            if (existingBookPath is not null)
                                pendingStrms.Add((existingBookPath, item));
                            Interlocked.Increment(ref libSkipped);
                            return;
                        }

                        Directory.CreateDirectory(bookDir);

                        if (BookFileNeedsDownload(bookPathNoExt))
                        {
                            DeleteExistingBookFiles(bookPathNoExt);
                            try
                            {
                                await connector.DownloadFileToPathAsync(item.RemoteId, bookPathNoExt, itemCt);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to download book '{Title}' — creating epub stub as fallback", item.Title);
                                _epubStubGenerator.Generate(item, config.Id, config.DisplayName, libraryId, libraryName, virtualLibRoot, proxyBaseUrl, config.LibraryOrganization, libraryType);
                            }
                        }

                        if (config.MetadataMode != MetadataMode.LocalScraping
                            && (config.MetadataMode == MetadataMode.RemoteSyncFull || !File.Exists(bookNfoPath)))
                        {
                            try
                            {
                                var bookMeta = await connector.GetMetadataAsync(item.RemoteId, itemCt);
                                _nfoGenerator.Generate(bookMeta, bookDir);
                                await DownloadArtworkAsync(connector, bookMeta, bookDir, itemCt, onExpectedPath: expectedSidecars.Add);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to fetch metadata for book '{Title}'", item.Title);
                            }
                        }

                        var newBookPath = FindExistingBookFile(bookPathNoExt);
                        if (newBookPath is not null)
                            pendingStrms.Add((newBookPath, item));
                        Interlocked.Increment(ref libCreated);
                        return;
                    }

                    // ---- All other media types -------------------------------------------
                    var strmPath      = _strmGenerator.Generate(item, config.Id, config.DisplayName, libraryId, libraryName, virtualLibRoot, proxyBaseUrl, config.LibraryOrganization, libraryType);
                    var nfoDir        = Path.GetDirectoryName(strmPath) ?? Path.Combine(virtualLibRoot, libraryName);
                    var mediaFileName = _strmGenerator.GetFileName(item);

                    // TV Show level (once per series — TryAdd guarantees a single winner)
                    if (item.Type == MediaType.Episode
                        && !string.IsNullOrEmpty(item.SeriesId)
                        && config.MetadataMode != MetadataMode.LocalScraping
                        && processedSeriesIds.TryAdd(item.SeriesId, true))
                    {
                        var showFolder = Path.GetDirectoryName(nfoDir);
                        if (showFolder is not null)
                        {
                            try
                            {
                                var showMeta = await connector.GetMetadataAsync(item.SeriesId, itemCt);
                                await DownloadArtworkAsync(connector, showMeta, showFolder, itemCt, onExpectedPath: expectedSidecars.Add);

                                var tvshowNfoPath = Path.Combine(showFolder, "tvshow.nfo");
                                expectedSidecars.Add(tvshowNfoPath);
                                if (config.MetadataMode == MetadataMode.RemoteSyncFull || !File.Exists(tvshowNfoPath))
                                {
                                    var nfoContent = _nfoGenerator.GenerateShowNfo(showMeta);
                                    if (!string.IsNullOrEmpty(nfoContent))
                                        File.WriteAllText(tvshowNfoPath, nfoContent, new System.Text.UTF8Encoding(false));
                                }

                                SyncUserFlagsForFolder(showFolder, showMeta, itemCt, config.LocalUserId);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to download show artwork for series '{Series}'", item.SeriesName);
                            }
                        }
                    }

                    // Season level (once per season)
                    if (item.Type == MediaType.Episode
                        && !string.IsNullOrEmpty(item.SeasonId)
                        && config.MetadataMode != MetadataMode.LocalScraping
                        && processedSeasonIds.TryAdd(item.SeasonId, true))
                    {
                        try
                        {
                            var seasonMeta = await connector.GetMetadataAsync(item.SeasonId, itemCt);
                            await DownloadArtworkAsync(connector, seasonMeta, nfoDir, itemCt, onExpectedPath: expectedSidecars.Add);

                            var seasonNfoPath = Path.Combine(nfoDir, "season.nfo");
                            expectedSidecars.Add(seasonNfoPath);
                            if (config.MetadataMode == MetadataMode.RemoteSyncFull || !File.Exists(seasonNfoPath))
                            {
                                var nfoContent = _nfoGenerator.GenerateSeasonNfo(seasonMeta);
                                if (!string.IsNullOrEmpty(nfoContent))
                                    File.WriteAllText(seasonNfoPath, nfoContent, new System.Text.UTF8Encoding(false));
                            }

                            SyncUserFlagsForFolder(nfoDir, seasonMeta, itemCt, config.LocalUserId);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to download season artwork for S{Season} of '{Series}'", item.SeasonNumber, item.SeriesName);
                        }
                    }

                    if (config.MetadataMode == MetadataMode.LocalScraping)
                    {
                        pendingStrms.Add((strmPath, item));
                        Interlocked.Increment(ref libCreated);
                        return;
                    }

                    var nfoPath = item.Type == MediaType.Movie
                        ? Path.Combine(nfoDir, "movie.nfo")
                        : Path.Combine(nfoDir, mediaFileName + ".nfo");
                    expectedSidecars.Add(nfoPath);

                    if (config.MetadataMode == MetadataMode.RemoteSync && File.Exists(nfoPath))
                    {
                        var hasFileinfo = NfoHasFileinfo(nfoPath);
                        _logger.LogDebug(
                            "VirtualLib skip '{Title}' — nfoHasFileinfo={HasFI} itemTech={HasTech} (codec={Codec} {W}x{H})",
                            item.Title, hasFileinfo,
                            item.Technical is not null,
                            item.Technical?.VideoCodec ?? item.Technical?.AudioCodec ?? "null",
                            item.Technical?.Width, item.Technical?.Height);
                        if (!hasFileinfo)
                            _nfoGenerator.PatchStreamDetails(nfoPath, item.Technical, item.RuntimeTicks);

                        pendingStrms.Add((strmPath, item));
                        Interlocked.Increment(ref libSkipped);
                        return;
                    }

                    MediaMetadata metadata;
                    try { metadata = await connector.GetMetadataAsync(item.RemoteId, itemCt); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get metadata for item {RemoteId} '{Title}' — will retry on next sync", item.RemoteId, item.Title);
                        Interlocked.Increment(ref libFailed);
                        return;
                    }

                    _nfoGenerator.Generate(metadata, nfoDir);
                    await DownloadArtworkAsync(connector, metadata, nfoDir, itemCt, onExpectedPath: expectedSidecars.Add);

                    _logger.LogDebug(
                        "VirtualLib NFO written for '{Title}' — Technical={HasTech} (codec={Codec} {W}x{H}) RuntimeTicks={Ticks}",
                        metadata.Title,
                        metadata.Technical is not null,
                        metadata.Technical?.VideoCodec ?? metadata.Technical?.AudioCodec ?? "null",
                        metadata.Technical?.Width,
                        metadata.Technical?.Height,
                        metadata.RuntimeTicks);

                    pendingStrms.Add((strmPath, metadata));
                    Interlocked.Increment(ref libCreated);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate files for item '{Title}'", item.Title);
                    Interlocked.Increment(ref libFailed);
                }
                finally
                {
                    var current = Interlocked.Increment(ref done);
                    progress?.Report(new SyncProgress { LibraryName = libraryName, Current = current, Total = total, RemoteTotal = rawItemCount, CurrentItem = item.Title });
                }
            });

        // --- Orphan cleanup (additive, #12/#44 — D11) ---------------------------------------
        // Guard rail #3 (CA11): abandon unconditionally if this library reported any failure or
        // if the remote listing itself came back empty — an unreachable/misbehaving remote server
        // must never be able to wipe a catalogue. Guard rails #1 (Enabled) and #2 (DryRun) live in
        // CleanupOptions and are enforced by LibraryCleanupService itself.
        if (orphanCleanupEnabled && _libraryCleanupService is not null && libFailed == 0 && rawItemCount > 0)
        {
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, _) in pendingStrms) expectedPaths.Add(path);
            foreach (var path in expectedSidecars) expectedPaths.Add(path);

            var libFolderPath = GetLibraryFolderPath(virtualLibRoot, config, libraryName, libraryType);

            try
            {
                // DryRun forced true for now — no persisted setting/UI to disable it yet (#12).
                var cleanupResult = await _libraryCleanupService.CleanupAsync(
                    libFolderPath, expectedPaths, new CleanupOptions { Enabled = true, DryRun = true }, ct);

                if (cleanupResult.OrphansFound.Count > 0)
                    _logger.LogWarning(
                        "Cleanup (dry-run) '{LibraryName}': {Count} orphan file(s) would be removed",
                        libraryName, cleanupResult.OrphansFound.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup failed for library '{LibraryName}'", libraryName);
            }
        }

        return (new LibrarySyncResult
        {
            LibraryName    = libraryName,
            ItemsCreated   = libCreated,
            ItemsSkipped   = libSkipped,
            ItemsFailed    = libFailed,
            ItemsFiltered  = itemsFiltered
        }, pendingStrms.ToList());
    }

    /// <summary>
    /// Applique <see cref="MediaFilterEngine"/> à la liste d'items d'une bibliothèque (#44).
    /// N'appelle <c>GetStreamInfoAsync</c> que si une règle de langue ou de sous-titres est active
    /// (D6) — une règle de résolution seule s'appuie sur <c>TechnicalInfo.Height</c>, déjà disponible.
    /// Fail-open (D7) : un item dont l'information est indéterminable est conservé et journalisé
    /// en décompte agrégé, jamais individuellement.
    /// </summary>
    private async Task<(IReadOnlyList<MediaItem> Items, int Rejected)> ApplyMediaFilterAsync(
        IMediaServerConnector connector,
        ConnectorConfig config,
        IReadOnlyList<MediaItem> items,
        string libraryName,
        CancellationToken ct)
    {
        var filter = config.MediaFilter;

        // Périmètre D8 : seuls Movie/Episode sont évalués — inutile de préparer un lot pour le reste.
        // On ne demande les pistes que pour les items qui n'en ont pas déjà (Emby les fournit déjà).
        var candidateIds = items
            .Where(i => i.Type is MediaType.Movie or MediaType.Episode)
            .Where(i => i.Technical is null || i.Technical.Streams.Count == 0)
            .Select(i => i.RemoteId)
            .ToList();

        // GetStreamInfoAsync n'est invoqué que si une règle de langue/sous-titres est active (D6).
        var needsStreamFetch = filter.AudioLanguages.Count > 0 || filter.SubtitleLanguages.Count > 0;

        IReadOnlyDictionary<string, IReadOnlyList<MediaStreamInfo>> fetchedStreams =
            new Dictionary<string, IReadOnlyList<MediaStreamInfo>>();

        if (needsStreamFetch && candidateIds.Count > 0)
        {
            try
            {
                fetchedStreams = await connector.GetStreamInfoAsync(candidateIds, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch stream info for library '{LibraryName}' — affected items will be treated as information unavailable (fail-open)",
                    libraryName);
            }
        }

        var kept = new List<MediaItem>(items.Count);
        int rejected = 0, unknownInfo = 0;

        foreach (var item in items)
        {
            IReadOnlyList<MediaStreamInfo> streams;
            if (item.Technical?.Streams is { Count: > 0 } technicalStreams)
                streams = technicalStreams;
            else
                streams = fetchedStreams.TryGetValue(item.RemoteId, out var fetched) ? fetched : Array.Empty<MediaStreamInfo>();

            var result = MediaFilterEngine.Evaluate(item, streams, filter);
            if (result.Status == FilterStatus.Rejected)
            {
                rejected++;
                continue;
            }

            if (result.Status == FilterStatus.KeptUnknownInfo)
                unknownInfo++;

            kept.Add(item);
        }

        if (unknownInfo > 0)
            _logger.LogWarning(
                "Filtre : {Count} item(s) conservés faute d'information technique dans '{LibraryName}'",
                unknownInfo, libraryName);

        _logger.LogInformation(
            "Filtre '{LibraryName}' : {Total} total, {Kept} retenus, {Rejected} écartés, {Unknown} conservés faute d'info",
            libraryName, items.Count, kept.Count, rejected, unknownInfo);

        return (kept, rejected);
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
        CancellationToken ct,
        string? localUserId = null)
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

            await Task.Delay(2000, ct).ConfigureAwait(false);
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
                    else if (baseItem is Audio audioTech)
                    {
                        if (tech.Bitrate.HasValue)             audioTech.TotalBitrate = tech.Bitrate.Value;
                        if (!string.IsNullOrEmpty(tech.Container)) audioTech.Container = tech.Container;
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

                    // Inject MediaStream entries directly into the DB — the only reliable
                    // way to populate "Media info" (codec/resolution/channels) for .strm files.
                    if (_itemRepository is not null && item.Technical is { } techForStreams)
                        SaveMediaStreams(baseItem.InternalId, techForStreams, item.RuntimeTicks, ct);
                    else
                        _logger.LogWarning(
                            "VirtualLib: SaveMediaStreams SKIPPED for '{Title}' — _itemRepository={R} Technical={T}",
                            item.Title, _itemRepository is not null, item.Technical is not null);

                    // Sync played/favorite/resume-position for the linked local user only
                    _logger.LogDebug(
                        "VirtualLib: user flags from source — '{Title}': played={P} count={PC} pos={Pos} fav={F}",
                        item.Title, item.IsPlayed, item.PlayCount, item.PlaybackPositionTicks, item.IsFavorite);

                    if (_userDataManager is not null && _userManager is not null
                        && (item.IsPlayed || item.IsFavorite || item.PlayCount > 0 || item.PlaybackPositionTicks > 0))
                    {
                        SyncUserFlags(baseItem, item, ct, localUserId);
                    }

                    // Patch NFO with <fileinfo><streamdetails> as additional persistence.
                    var nfoDir2 = Path.GetDirectoryName(strmPath)!;
                    var nfoPath = item.Type == MediaType.Movie
                        ? Path.Combine(nfoDir2, "movie.nfo")
                        : Path.ChangeExtension(strmPath, ".nfo");
                    _nfoGenerator.PatchStreamDetails(nfoPath, item.Technical, item.RuntimeTicks);

                    pushed++;
                    _logger.LogInformation(
                        "VirtualLib: pushed metadata — '{Path}' type={Type} ticks={Ticks} tech={HasTech}",
                        strmPath, baseItem.GetType().Name, item.RuntimeTicks, item.Technical is not null);
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

    /// <summary>
    /// Builds and persists MediaStream entries (video + audio) from TechnicalInfo
    /// directly into the Emby item repository, bypassing ffprobe.
    /// This populates the "Media info" panel in the Emby UI for .strm files.
    /// </summary>
    /// <summary>
    /// Builds and persists MediaStream entries from TechnicalInfo. When <see cref="TechnicalInfo.Streams"/>
    /// is populated (#44 D10), the full list (all audio tracks, all subtitles) is used. When it is
    /// empty — a connector or item that hasn't supplied the detailed list — this falls back
    /// <b>exactly</b> to the pre-#44 behaviour: one reconstructed video stream and one audio stream
    /// from the scalar fields. No regression possible either way.
    /// </summary>
    private void SaveMediaStreams(long itemId, TechnicalInfo tech, long? runtimeTicks, CancellationToken ct)
    {
        try
        {
            List<EmbyMediaStream> streams;

            if (tech.Streams.Count > 0)
            {
                streams = tech.Streams
                    .Select((s, i) => MapMediaStreamInfoToEmby(s, i))
                    .ToList();
            }
            else
            {
                streams = new List<EmbyMediaStream>();
                int index = 0;

                bool hasVideo = tech.VideoCodec is not null || tech.Width.HasValue || tech.Height.HasValue;
                if (hasVideo)
                {
                    var video = new EmbyMediaStream
                    {
                        Type      = EmbyMediaStreamType.Video,
                        Index     = index++,
                        IsDefault = true,
                    };
                    if (!string.IsNullOrEmpty(tech.VideoCodec)) video.Codec = tech.VideoCodec;
                    if (tech.Width.HasValue)   video.Width   = tech.Width.Value;
                    if (tech.Height.HasValue)  video.Height  = tech.Height.Value;
                    if (tech.Bitrate.HasValue) video.BitRate = tech.Bitrate.Value;
                    streams.Add(video);
                }

                bool hasAudio = tech.AudioCodec is not null || tech.AudioChannels.HasValue;
                if (hasAudio)
                {
                    var audio = new EmbyMediaStream
                    {
                        Type      = EmbyMediaStreamType.Audio,
                        Index     = index++,
                        IsDefault = true,
                    };
                    if (!string.IsNullOrEmpty(tech.AudioCodec))  audio.Codec      = tech.AudioCodec;
                    if (tech.AudioChannels.HasValue)              audio.Channels   = tech.AudioChannels.Value;
                    if (tech.AudioSampleRate.HasValue)            audio.SampleRate = tech.AudioSampleRate.Value;
                    streams.Add(audio);
                }
            }

            if (streams.Count > 0)
            {
                _itemRepository!.SaveMediaStreams(itemId, streams, ct);
                _logger.LogInformation(
                    "VirtualLib: saved {Count} MediaStream(s) for itemId={ItemId} (source={Source})",
                    streams.Count, itemId, tech.Streams.Count > 0 ? "full-list" : "scalar-fallback");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VirtualLib: SaveMediaStreams failed for itemId={ItemId}", itemId);
        }
    }

    private static EmbyMediaStream MapMediaStreamInfoToEmby(MediaStreamInfo s, int index)
    {
        var stream = new EmbyMediaStream
        {
            Type = s.Kind switch
            {
                MediaStreamKind.Video    => EmbyMediaStreamType.Video,
                MediaStreamKind.Audio    => EmbyMediaStreamType.Audio,
                MediaStreamKind.Subtitle => EmbyMediaStreamType.Subtitle,
                _                        => EmbyMediaStreamType.Unknown
            },
            Index             = index,
            IsDefault         = s.IsDefault,
            IsForced          = s.IsForced,
            IsExternal        = s.IsExternal,
            IsHearingImpaired = s.IsHearingImpaired
        };
        if (!string.IsNullOrEmpty(s.Codec))    stream.Codec    = s.Codec;
        if (!string.IsNullOrEmpty(s.Language)) stream.Language = s.Language;
        if (s.Width.HasValue)                  stream.Width    = s.Width.Value;
        if (s.Height.HasValue)                 stream.Height   = s.Height.Value;
        if (s.Channels.HasValue)                stream.Channels = s.Channels.Value;
        return stream;
    }

    /// <summary>
    /// Finds a folder item by path in the local library and syncs its user flags.
    /// No-op if the folder isn't in Emby yet (first sync) or if all flags are unset.
    /// </summary>
    private void SyncUserFlagsForFolder(string folderPath, MediaItem item, CancellationToken ct, string? localUserId = null)
    {
        if (_libraryManager is null || _userDataManager is null || _userManager is null) return;
        if (!item.IsPlayed && !item.IsFavorite && item.PlayCount == 0 && item.PlaybackPositionTicks == 0) return;

        try
        {
            var folder = _libraryManager.FindByPath(folderPath, true);
            if (folder is null) return; // not yet scanned — will be picked up on next sync
            SyncUserFlags(folder, item, ct, localUserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VirtualLib: SyncUserFlagsForFolder failed for '{Path}'", folderPath);
        }
    }

    /// <summary>
    /// Copies played/favourite state from the remote item to the linked local Emby user.
    /// When <paramref name="localUserId"/> is null or empty, the sync is skipped entirely.
    /// Only called when at least one flag is set on the remote side.
    /// </summary>
    private void SyncUserFlags(BaseItem baseItem, MediaItem item, CancellationToken ct, string? localUserId = null)
    {
        if (string.IsNullOrEmpty(localUserId))
        {
            _logger.LogDebug(
                "VirtualLib: SyncUserFlags skipped for '{Title}' — no LocalUserId configured on connector",
                item.Title);
            return;
        }

        _logger.LogInformation(
            "VirtualLib: SyncUserFlags '{Title}' — played={P} playCount={PC} positionTicks={Pos} favorite={F} → localUser={U}",
            item.Title, item.IsPlayed, item.PlayCount, item.PlaybackPositionTicks, item.IsFavorite, localUserId);

        try
        {
#pragma warning disable CS0618 // IUserManager.Users is deprecated but the replacement (GetUsers) requires a UserQuery filter
            var normalizedTarget = Guid.TryParse(localUserId, out var tg) ? tg.ToString("N") : localUserId;
            var user = _userManager!.Users.FirstOrDefault(u =>
                string.Equals(u.Id.ToString("N"), normalizedTarget, StringComparison.OrdinalIgnoreCase));
#pragma warning restore CS0618
            if (user is null)
            {
                _logger.LogWarning("VirtualLib: SyncUserFlags — local user '{UserId}' not found, skipping", localUserId);
                return;
            }

            var userData = _userDataManager!.GetUserData(user, baseItem);

                // Only update if the remote state differs (avoid unnecessary writes)
                bool changed = false;
                if (item.IsPlayed && !userData.Played)
                {
                    userData.Played = true;
                    changed = true;
                }
                if (item.PlayCount > userData.PlayCount)
                {
                    userData.PlayCount = item.PlayCount;
                    changed = true;
                }
                if (item.LastPlayedDate.HasValue
                    && (!userData.LastPlayedDate.HasValue || item.LastPlayedDate > userData.LastPlayedDate))
                {
                    userData.LastPlayedDate = item.LastPlayedDate;
                    changed = true;
                }
                if (item.IsFavorite && !userData.IsFavorite)
                {
                    userData.IsFavorite = true;
                    changed = true;
                }
                // Resume position: only advance (never rewind what the local user already played past)
                if (item.PlaybackPositionTicks > userData.PlaybackPositionTicks)
                {
                    userData.PlaybackPositionTicks = item.PlaybackPositionTicks;
                    changed = true;
                }

                if (changed)
                    _userDataManager.SaveUserData(user, baseItem, userData, EmbyUserDataSaveReason.Import, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VirtualLib: SyncUserFlags failed for item '{Title}'", item.Title);
        }
    }

    private static string SanitizeFileName(string name) => StrmGenerator.SanitizeName(name);

    private static bool NfoHasFileinfo(string nfoPath)
    {
        try { return File.ReadAllText(nfoPath).Contains("<fileinfo>", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static readonly string[] _bookExtensions = { ".epub", ".mobi", ".pdf", ".azw3", ".cbz", ".cbr", ".fb2" };

    private static string? FindExistingBookFile(string pathNoExt)
    {
        foreach (var ext in _bookExtensions)
        {
            var path = pathNoExt + ext;
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // Returns true when no real book file exists yet, or only a tiny stub is present (<4 KB)
    private static bool BookFileNeedsDownload(string pathNoExt)
    {
        foreach (var ext in _bookExtensions)
        {
            var path = pathNoExt + ext;
            if (File.Exists(path))
                return new FileInfo(path).Length < 4096;
        }
        return true;
    }

    private static void DeleteExistingBookFiles(string pathNoExt)
    {
        foreach (var ext in _bookExtensions)
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
        bool audiobook = false,
        Action<string>? onExpectedPath = null)
    {
        var artworkFileNames = audiobook ? _audioArtworkFileNames : _videoArtworkFileNames;

        foreach (var artworkType in item.AvailableArtwork)
        {
            ct.ThrowIfCancellationRequested();

            if (!artworkFileNames.TryGetValue(artworkType, out var fileName)) continue;

            var destPath = Path.Combine(targetDir, fileName);
            // Reported whether newly downloaded, already present, or the download below fails —
            // this is the path a well-formed sync expects to exist (orphan cleanup, #44/D11).
            onExpectedPath?.Invoke(destPath);
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
