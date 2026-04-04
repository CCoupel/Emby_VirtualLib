using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core;

namespace VirtualLib;

/// <summary>
/// Emby scheduled task — runs a full sync of all enabled connectors
/// on the interval configured in the plugin settings.
/// </summary>
public sealed class LibrarySyncJob : IScheduledTask, IConfigurableScheduledTask
{
    private readonly SyncService _syncService;
    private readonly ILibraryManager _libraryManager;

    public LibrarySyncJob(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
        _syncService = new SyncService(
            new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance),
            new StrmGenerator(),
            new EpubStubGenerator(),
            new NfoGenerator(),
            NullLogger<SyncService>.Instance,
            libraryManager);
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

        var virtualLibRoot = config.VirtualLibraryRootPath;
        var proxyBaseUrl   = config.ProxyBaseUrl;

        int totalCreated = 0;
        double step = 100.0 / enabledConnectors.Count;
        var allPending = new List<LibraryPendingMetadata>();

        for (var i = 0; i < enabledConnectors.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connectorConfig = enabledConnectors[i];

            var syncProgress = new Progress<SyncProgress>(p =>
            {
                var pct = (i * step) + (step * p.Current / Math.Max(p.Total, 1));
                progress.Report(pct);
            });

            var (result, pending) = await _syncService.SyncConnectorAsync(
                connectorConfig,
                virtualLibRoot,
                proxyBaseUrl,
                syncProgress,
                cancellationToken);

            totalCreated += result.ItemsCreated;
            allPending.AddRange(pending);
            progress.Report((i + 1) * step);
        }

        if (totalCreated > 0)
        {
            _libraryManager.QueueLibraryScan();
            // Fire-and-forget metadata push (phase 2) for the scheduled task
            var snapshot = allPending;
            _ = Task.Run(async () =>
            {
                foreach (var g in snapshot)
                    await _syncService.PushMetadataAsync(g.Items, g.LibraryName, null, CancellationToken.None);
            });
        }

        progress.Report(100);
    }

    // -------------------------------------------------------------------------
    // IConfigurableScheduledTask
    // -------------------------------------------------------------------------

    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
}
