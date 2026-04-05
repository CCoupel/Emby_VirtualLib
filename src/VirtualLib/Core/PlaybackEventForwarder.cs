using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    // PlaySessionId par clé "connectorId:remoteItemId" — généré au Start, réutilisé pour Progress/Stop
    private readonly ConcurrentDictionary<string, string> _playSessions = new();

    public PlaybackEventForwarder(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        _logger = NullLoggerFactory.Instance.CreateLogger<PlaybackEventForwarder>();
        _connectorFactory = new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance);
    }

    public void Run()
    {
        _sessionManager.PlaybackStart    += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped  += OnPlaybackStopped;
        _logger.LogInformation("[VirtualLib] PlaybackEventForwarder started");
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
        Console.Error.WriteLine($"[VirtualLib] PlaybackEventForwarder — {eventType} path={path ?? "(null)"}");
        _logger.LogInformation("[VirtualLib] {Event} — item path: {Path}", eventType, path ?? "(null)");

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
        if (!match.Success)
        {
            Console.Error.WriteLine($"[VirtualLib] .strm URL not matched: {strmContent.Trim()}");
            return;
        }

        var connectorId  = match.Groups[1].Value;
        var remoteItemId = match.Groups[3].Value;
        Console.Error.WriteLine($"[VirtualLib] Matched — connector={connectorId} item={remoteItemId}");

        var connectorConfig = Plugin.Instance?.Configuration.Connectors
            .FirstOrDefault(c => c.Id == connectorId);
        if (connectorConfig is null || !connectorConfig.Enabled)
        {
            Console.Error.WriteLine($"[VirtualLib] Connector not found or disabled: {connectorId}");
            return;
        }

        // Récupère ou crée le connecteur (réutilise la session auth)
        IMediaServerConnector connector;
        try
        {
            connector = _connectors.GetOrAdd(connectorId, _ => _connectorFactory.Create(connectorConfig));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualLib] Failed to create connector {connectorId}: {ex.GetBaseException().Message}");
            return;
        }

        var positionTicks  = e.PlaybackPositionTicks ?? 0L;
        var isPaused       = e.IsPaused;
        var sessionKey     = $"{connectorId}:{remoteItemId}";

        try
        {
            switch (eventType)
            {
                case PlaybackEvent.Start:
                {
                    var playSessionId = Guid.NewGuid().ToString("N");
                    _playSessions[sessionKey] = playSessionId;
                    await connector.ReportPlaybackStartAsync(remoteItemId, playSessionId).ConfigureAwait(false);
                    Console.Error.WriteLine($"[VirtualLib] PlaybackStart OK → connector={connectorId} item={remoteItemId} session={playSessionId}");
                    break;
                }

                case PlaybackEvent.Progress:
                {
                    if (!_playSessions.TryGetValue(sessionKey, out var playSessionId)) return;
                    await connector.ReportPlaybackProgressAsync(remoteItemId, playSessionId, positionTicks, isPaused).ConfigureAwait(false);
                    break;
                }

                case PlaybackEvent.Stop:
                {
                    _playSessions.TryRemove(sessionKey, out var playSessionId);
                    playSessionId ??= Guid.NewGuid().ToString("N"); // fallback si Start manqué (pod restart)
                    await connector.ReportPlaybackStoppedAsync(remoteItemId, playSessionId).ConfigureAwait(false);
                    Console.Error.WriteLine($"[VirtualLib] PlaybackStopped OK → connector={connectorId} item={remoteItemId}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualLib] Failed to forward {eventType} for item={remoteItemId}: {ex.GetBaseException().Message}");
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
