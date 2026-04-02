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
    private static readonly SyncService _syncService = new(
        new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance),
        new StrmGenerator(),
        new EpubStubGenerator(),
        new NfoGenerator(),
        NullLogger<SyncService>.Instance);

    private readonly ILibraryManager _libraryManager;

    public LibrarySyncJob(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
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

        for (var i = 0; i < enabledConnectors.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connectorConfig = enabledConnectors[i];

            var syncProgress = new Progress<SyncProgress>(p =>
            {
                var pct = (i * step) + (step * p.Current / Math.Max(p.Total, 1));
                progress.Report(pct);
            });

            var result = await _syncService.SyncConnectorAsync(
                connectorConfig,
                virtualLibRoot,
                proxyBaseUrl,
                syncProgress,
                cancellationToken);

            totalCreated += result.ItemsCreated;
            progress.Report((i + 1) * step);
        }

        if (totalCreated > 0)
            _libraryManager.QueueLibraryScan();

        progress.Report(100);
    }

    // -------------------------------------------------------------------------
    // IConfigurableScheduledTask
    // -------------------------------------------------------------------------

    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
}
