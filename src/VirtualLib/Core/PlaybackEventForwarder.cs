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
///
/// Un heartbeat indépendant envoie Progress au remote toutes les 30s pour
/// chaque session active, même si le client host est en pause (les clients
/// Emby cessent d'envoyer des événements de progression lors d'une pause
/// prolongée, ce qui entraîne un idle timeout côté serveur distant).
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

    // État de chaque session active, par clé "connectorId:remoteItemId"
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();

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

        if (string.IsNullOrEmpty(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return;

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

        var positionTicks = e.PlaybackPositionTicks ?? 0L;
        var isPaused      = e.IsPaused;
        var sessionKey    = $"{connectorId}:{remoteItemId}";

        try
        {
            switch (eventType)
            {
                case PlaybackEvent.Start:
                {
                    // Annuler un heartbeat précédent si la session était déjà ouverte
                    if (_sessions.TryRemove(sessionKey, out var prev))
                        prev.Cts.Cancel();

                    var playSessionId = Guid.NewGuid().ToString("N");
                    var cts = new CancellationTokenSource();
                    _sessions[sessionKey] = new ActiveSession(playSessionId, positionTicks, isPaused, cts);

                    await connector.ReportPlaybackStartAsync(remoteItemId, playSessionId).ConfigureAwait(false);
                    Console.Error.WriteLine($"[VirtualLib] PlaybackStart OK → connector={connectorId} item={remoteItemId} session={playSessionId}");

                    // Lancer le heartbeat indépendant (toutes les 30s)
                    _ = Task.Run(() => HeartbeatLoopAsync(connector, remoteItemId, sessionKey, cts.Token));
                    break;
                }

                case PlaybackEvent.Progress:
                {
                    // Mettre à jour la position/pause — le heartbeat lira la nouvelle valeur
                    if (_sessions.TryGetValue(sessionKey, out var session))
                    {
                        _sessions[sessionKey] = session with { PositionTicks = positionTicks, IsPaused = isPaused };
                        await connector.ReportPlaybackProgressAsync(remoteItemId, session.PlaySessionId, positionTicks, isPaused).ConfigureAwait(false);
                    }
                    break;
                }

                case PlaybackEvent.Stop:
                {
                    if (_sessions.TryRemove(sessionKey, out var session))
                    {
                        session.Cts.Cancel();
                        await connector.ReportPlaybackStoppedAsync(remoteItemId, session.PlaySessionId, positionTicks).ConfigureAwait(false);
                        Console.Error.WriteLine($"[VirtualLib] PlaybackStopped OK → connector={connectorId} item={remoteItemId} pos={positionTicks}");
                    }
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
    // Heartbeat — maintient la session remote vivante toutes les 30s
    // -------------------------------------------------------------------------

    private async Task HeartbeatLoopAsync(
        IMediaServerConnector connector,
        string remoteItemId,
        string sessionKey,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;
                if (!_sessions.TryGetValue(sessionKey, out var session)) break;

                await connector.ReportPlaybackProgressAsync(
                    remoteItemId, session.PlaySessionId, session.PositionTicks, session.IsPaused, ct)
                    .ConfigureAwait(false);

                Console.Error.WriteLine(
                    $"[VirtualLib] Heartbeat → item={remoteItemId} pos={session.PositionTicks} paused={session.IsPaused}");
            }
        }
        catch (OperationCanceledException) { /* session arrêtée normalement */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualLib] Heartbeat error for item={remoteItemId}: {ex.GetBaseException().Message}");
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

        foreach (var session in _sessions.Values)
            session.Cts.Cancel();
        _sessions.Clear();

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

    private sealed record ActiveSession(
        string PlaySessionId,
        long PositionTicks,
        bool IsPaused,
        CancellationTokenSource Cts);
}
