using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core;

namespace VirtualLib;

/// <summary>
/// Emby scheduled task — runs a full parallel sync of all enabled connectors
/// on the interval configured in the plugin settings.
/// </summary>
public sealed class LibrarySyncJob : IScheduledTask, IConfigurableScheduledTask
{
    private readonly SyncService _syncService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;

    public LibrarySyncJob(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IItemRepository itemRepository, IUserDataManager userDataManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _syncService = new SyncService(
            new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance),
            new StrmGenerator(),
            new EpubStubGenerator(),
            new NfoGenerator(),
            NullLogger<SyncService>.Instance,
            libraryManager,
            itemRepository,
            userDataManager,
            userManager);
    }

    // -------------------------------------------------------------------------
    // IScheduledTask
    // -------------------------------------------------------------------------

    public string Name => "VirtualLib — Auto Sync";
    public string Description => "Synchronises all enabled remote connectors and downloads metadata/artwork.";
    public string Category => "VirtualLib";
    public string Key => "VirtualLibAutoSync";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var intervalHours = Plugin.Instance?.Configuration.SyncIntervalHours ?? 6;
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerInterval,
            IntervalTicks = TimeSpan.FromHours(intervalHours).Ticks
        };
    }

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return;

        var enabledConnectors = config.Connectors.Where(c => c.Enabled).ToList();
        if (enabledConnectors.Count == 0) return;

        var root     = config.VirtualLibraryRootPath;
        var proxyUrl = config.ProxyBaseUrl;

        // Register all libraries (allows polling the config page during scheduled sync)
        if (!SyncState.TryStart()) return; // another sync already running
        foreach (var conn in enabledConnectors)
        foreach (var libId in conn.LibraryIds)
        {
            var known = conn.KnownLibraries.FirstOrDefault(l => l.Id == libId);
            SyncState.RegisterLibrary(conn.Id, conn.DisplayName, libId,
                known?.Name ?? libId, known?.Type ?? string.Empty);
        }

        int totalLibs = enabledConnectors.Sum(c => c.LibraryIds.Count);
        int doneLibs  = 0;

        var results   = new System.Collections.Concurrent.ConcurrentBag<SyncResult>();
        var semaphores = enabledConnectors.ToDictionary(
            c => c.Id,
            c => new SemaphoreSlim(Math.Max(1, c.MaxParallelLibraries)));

        var syncSvc = _syncService;
        var libMon  = _libraryMonitor;

        var allTasks = enabledConnectors
            .SelectMany(conn => conn.LibraryIds.Select(libId => Task.Run(async () =>
            {
                await SyncLibraryAutonomousAsync(conn, libId, root, proxyUrl, syncSvc, libMon,
                    semaphores[conn.Id], results, cancellationToken);

                var done = Interlocked.Increment(ref doneLibs);
                progress.Report(done * 100.0 / Math.Max(totalLibs, 1));
            }, cancellationToken)))
            .ToList();

        await Task.WhenAll(allTasks);

        config.Connectors = Plugin.Instance?.Configuration.Connectors ?? config.Connectors;
        Plugin.Instance?.SaveConfiguration();

        SyncState.Finish(results.ToList());
        progress.Report(100);
    }

    // -------------------------------------------------------------------------
    // IConfigurableScheduledTask
    // -------------------------------------------------------------------------

    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    // -------------------------------------------------------------------------
    // Private — mirrors ConfigController.SyncLibraryAutonomousAsync
    // -------------------------------------------------------------------------

    private static async Task SyncLibraryAutonomousAsync(
        ConnectorConfig conn,
        string libraryId,
        string root,
        string proxyUrl,
        SyncService syncSvc,
        ILibraryMonitor libMon,
        SemaphoreSlim semaphore,
        System.Collections.Concurrent.ConcurrentBag<SyncResult> results,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var prog1 = new Progress<SyncProgress>(p =>
                SyncState.UpdatePhase1(conn.Id, libraryId, p.Current, p.Total));

            var (result, pending) = await syncSvc.SyncLibraryAsync(
                conn, libraryId, root, proxyUrl, prog1, ct);

            results.Add(result);

            var lp = pending.FirstOrDefault();
            if (lp is not null && Directory.Exists(lp.LibraryFolderPath))
                libMon.ReportFileSystemChanged(lp.LibraryFolderPath);

            if (result.Success)
            {
                var total = result.ItemsCreated + result.ItemsSkipped + result.ItemsFailed;
                SyncState.UpdatePhase1(conn.Id, libraryId, total, total);
            }

            semaphore.Release();

            if (lp is not null && lp.Items.Count > 0)
            {
                SyncState.Phase2Start(conn.Id, libraryId, lp.Items.Count);
                await Task.Delay(5_000, ct);

                var prog2 = new Progress<SyncProgress>(p =>
                    SyncState.UpdatePhase2(conn.Id, libraryId, p.Current, p.Total));

                await syncSvc.PushMetadataAsync(lp.Items, lp.LibraryName, prog2, ct);
            }

            SyncState.MarkDone(conn.Id, libraryId);
        }
        catch (OperationCanceledException)
        {
            SyncState.MarkFailed(conn.Id, libraryId, "Cancelled");
            try { semaphore.Release(); } catch { /* already released */ }
            throw;
        }
        catch (Exception ex)
        {
            SyncState.MarkFailed(conn.Id, libraryId, ex.Message);
            results.Add(SyncResult.Failure(conn.DisplayName, ex.Message, TimeSpan.Zero));
            try { semaphore.Release(); } catch { /* already released */ }
        }
    }
}
