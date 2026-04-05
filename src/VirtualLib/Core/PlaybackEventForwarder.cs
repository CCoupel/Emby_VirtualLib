using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Core;

/// <summary>
/// Service démarré au lancement d'Emby (IServerEntryPoint).
///
/// S'abonne aux events de session Emby pour propager les notifications
/// de lecture (start, progress, stop) vers le serveur distant correspondant
/// lorsque l'item joué provient d'une bibliothèque virtuelle VirtualLib.
///
/// Le mapping local → remote se fait en lisant le contenu du fichier .strm,
/// qui contient l'URL proxy :
///   {baseUrl}/virtuallib/proxy/{connectorId}/{libraryId}/{remoteItemId}
/// </summary>
public sealed class PlaybackEventForwarder : IServerEntryPoint
{
    // Regex pour extraire connectorId / libraryId / remoteItemId depuis l'URL proxy
    private static readonly Regex ProxyUrlRegex = new(
        @"/virtuallib/proxy/([^/\s]+)/([^/\s]+)/([^/\s\?]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PlaybackEventForwarder> _logger;
    private readonly IConnectorFactory _connectorFactory;

    // Connecteurs mis en cache par connectorId pour réutiliser la session auth
    private readonly ConcurrentDictionary<string, IMediaServerConnector> _connectors = new();

    public PlaybackEventForwarder(
        ISessionManager sessionManager,
        ILoggerFactory loggerFactory)
    {
        _sessionManager = sessionManager;
        _logger = loggerFactory.CreateLogger<PlaybackEventForwarder>();
        _connectorFactory = new ConnectorFactory(new DefaultHttpClientFactory(), loggerFactory);
    }

    public Task Run()
    {
        _sessionManager.PlaybackStart    += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped  += OnPlaybackStopped;
        _logger.LogInformation("[VirtualLib] PlaybackEventForwarder started");
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Event handlers — fire-and-forget pour ne jamais bloquer le thread Emby
    // -------------------------------------------------------------------------

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        => FireAndForget(() => ForwardEventAsync(e, PlaybackEvent.Start));

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        => FireAndForget(() => ForwardEventAsync(e, PlaybackEvent.Progress));

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        => FireAndForget(() => ForwardEventAsync(e, PlaybackEvent.Stop));

    // -------------------------------------------------------------------------
    // Core forwarding logic
    // -------------------------------------------------------------------------

    private async Task ForwardEventAsync(PlaybackProgressEventArgs e, PlaybackEvent eventType)
    {
        var path = e.Item?.Path;
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return;

        // Lire le contenu du .strm pour extraire l'URL proxy
        string strmContent;
        try
        {
            strmContent = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[VirtualLib] Cannot read .strm file: {Path}", path);
            return;
        }

        var match = ProxyUrlRegex.Match(strmContent.Trim());
        if (!match.Success) return;  // Pas une URL VirtualLib

        var connectorId  = match.Groups[1].Value;
        var remoteItemId = match.Groups[3].Value;

        var connectorConfig = Plugin.Instance?.Configuration.Connectors
            .FirstOrDefault(c => c.Id == connectorId);
        if (connectorConfig is null || !connectorConfig.Enabled) return;

        // Récupère ou crée le connecteur (réutilise la session auth)
        var connector = _connectors.GetOrAdd(connectorId, _ => _connectorFactory.Create(connectorConfig));

        var positionTicks = e.PlaybackPositionTicks ?? 0L;
        var isPaused      = e.IsPaused;

        try
        {
            switch (eventType)
            {
                case PlaybackEvent.Start:
                    await connector.ReportPlaybackStartAsync(remoteItemId).ConfigureAwait(false);
                    _logger.LogDebug("[VirtualLib] PlaybackStart → connector={C} item={I}", connectorId, remoteItemId);
                    break;

                case PlaybackEvent.Progress:
                    await connector.ReportPlaybackProgressAsync(remoteItemId, positionTicks, isPaused).ConfigureAwait(false);
                    _logger.LogDebug("[VirtualLib] PlaybackProgress → connector={C} item={I} pos={T}", connectorId, remoteItemId, positionTicks);
                    break;

                case PlaybackEvent.Stop:
                    await connector.ReportPlaybackStoppedAsync(remoteItemId).ConfigureAwait(false);
                    _logger.LogDebug("[VirtualLib] PlaybackStopped → connector={C} item={I} pos={T}", connectorId, remoteItemId, positionTicks);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[VirtualLib] Failed to forward {Event} for item={I}", eventType, remoteItemId);
        }
    }

    // -------------------------------------------------------------------------
    // Dispose / Cleanup
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _sessionManager.PlaybackStart    -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped  -= OnPlaybackStopped;

        foreach (var connector in _connectors.Values)
            connector.Dispose();
        _connectors.Clear();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void FireAndForget(Func<Task> action)
    {
        Task.Run(action).ContinueWith(
            t => Console.Error.WriteLine($"[VirtualLib] PlaybackEventForwarder unhandled: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private enum PlaybackEvent { Start, Progress, Stop }
}
