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
/// Comportements clés :
/// - Heartbeat 30s : maintient la session remote vivante pendant les pauses.
/// - Debounce Stop 8s : le host Emby fire PlaybackStopped quand le proxy
///   stream HTTP se ferme (client a bufferisé), même si l'utilisateur est
///   encore actif. On attend 8s — mais uniquement si aucun Progress n'arrive.
/// - Re-ouverture transparente : si un Progress arrive pour une session
///   absente (session expirée, debounce expiré, ou pod redémarré), on
///   ré-envoie Start + Progress automatiquement sans attendre un Start du host.
/// - Fix race condition : le debounce Stop vérifie le PlaySessionId courant
///   avant de supprimer, pour éviter de tuer une nouvelle session.
/// </summary>
public sealed class PlaybackEventForwarder : IServerEntryPoint
{
    private static readonly Regex ProxyUrlRegex = new(
        @"/virtuallib/proxy/([^/\s]+)/([^/\s]+)/([^/\s\?]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PlaybackEventForwarder> _logger;
    private readonly IConnectorFactory _connectorFactory;

    private readonly ConcurrentDictionary<string, IMediaServerConnector> _connectors = new();
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
    // Event handlers
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
        Console.Error.WriteLine($"[VirtualLib] {eventType} path={path ?? "(null)"}");

        if (string.IsNullOrEmpty(path) || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            return;

        string strmContent;
        try { strmContent = await File.ReadAllTextAsync(path).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Cannot read .strm: {Path}", path); return; }

        var match = ProxyUrlRegex.Match(strmContent.Trim());
        if (!match.Success) { Console.Error.WriteLine($"[VirtualLib] .strm URL not matched: {strmContent.Trim()}"); return; }

        var connectorId  = match.Groups[1].Value;
        var remoteItemId = match.Groups[3].Value;

        var connectorConfig = Plugin.Instance?.Configuration.Connectors.FirstOrDefault(c => c.Id == connectorId);
        if (connectorConfig is null || !connectorConfig.Enabled) return;

        IMediaServerConnector connector;
        try { connector = _connectors.GetOrAdd(connectorId, _ => _connectorFactory.Create(connectorConfig)); }
        catch (Exception ex) { Console.Error.WriteLine($"[VirtualLib] Connector error {connectorId}: {ex.GetBaseException().Message}"); return; }

        var positionTicks = e.PlaybackPositionTicks ?? 0L;
        var isPaused      = e.IsPaused;
        // Normalize both sides to "N" (no-dash) GUID format for reliable comparison
        var rawUserId   = e.Session?.UserId ?? "";
        var localUserId = Guid.TryParse(rawUserId, out var sessionGuid) ? sessionGuid.ToString("N") : rawUserId;
        var sessionKey  = $"{connectorId}:{remoteItemId}:{localUserId}";
        var configUserId = Guid.TryParse(connectorConfig.LocalUserId ?? "", out var cfgGuid) ? cfgGuid.ToString("N") : (connectorConfig.LocalUserId ?? "");
        var isLinkedUser = !string.IsNullOrEmpty(configUserId) && configUserId == localUserId;
        // "cyril@Emby Web" — identifie le client local tel qu'il apparaît côté serveur distant
        var deviceName = $"{e.Session?.UserName ?? localUserId}@{e.Session?.Client ?? "VirtualLib"}";

        try
        {
            switch (eventType)
            {
                case PlaybackEvent.Start:
                    await HandleStartAsync(connector, remoteItemId, sessionKey, positionTicks, isPaused, deviceName);
                    break;

                case PlaybackEvent.Progress:
                    await HandleProgressAsync(connector, remoteItemId, sessionKey, positionTicks, isPaused);
                    break;

                case PlaybackEvent.Stop:
                    HandleStop(connector, remoteItemId, sessionKey, positionTicks, isLinkedUser);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualLib] {eventType} error for {remoteItemId}: {ex.GetBaseException().Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Handlers per event type
    // -------------------------------------------------------------------------

    private async Task HandleStartAsync(
        IMediaServerConnector connector, string remoteItemId, string sessionKey,
        long positionTicks, bool isPaused, string deviceName)
    {
        if (_sessions.TryRemove(sessionKey, out var prev))
        {
            prev.PendingStopCts?.Cancel();
            prev.HeartbeatCts.Cancel();
        }

        var session = OpenSession(sessionKey, positionTicks, isPaused, deviceName);
        await connector.ReportPlaybackStartAsync(remoteItemId, session.PlaySessionId, session.DeviceName).ConfigureAwait(false);
        Console.Error.WriteLine($"[VirtualLib] Start OK → item={remoteItemId} session={session.PlaySessionId} device={session.DeviceName}");
        _ = Task.Run(() => HeartbeatLoopAsync(connector, remoteItemId, sessionKey, session.HeartbeatCts.Token));
    }

    private async Task HandleProgressAsync(
        IMediaServerConnector connector, string remoteItemId, string sessionKey,
        long positionTicks, bool isPaused)
    {
        _sessions.TryGetValue(sessionKey, out var session);

        if (session is null)
        {
            // Session absente : debounce expiré, pod redémarré, ou host n'a pas renvoyé Start.
            // Réouvrir transparentement sans deviceName connu — fallback vide.
            Console.Error.WriteLine($"[VirtualLib] Progress sans session active — réouverture pour item={remoteItemId}");
            session = OpenSession(sessionKey, positionTicks, isPaused, "VirtualLib");
            await connector.ReportPlaybackStartAsync(remoteItemId, session.PlaySessionId, session.DeviceName).ConfigureAwait(false);
            _ = Task.Run(() => HeartbeatLoopAsync(connector, remoteItemId, sessionKey, session.HeartbeatCts.Token));
        }
        else if (session.PendingStopCts != null)
        {
            // Stop pending — le client est encore actif, annuler
            session.PendingStopCts.Cancel();
            session = session with { PendingStopCts = null };
            _sessions[sessionKey] = session;
            Console.Error.WriteLine($"[VirtualLib] Stop annulé (client actif) → item={remoteItemId}");
        }

        _sessions[sessionKey] = session with { PositionTicks = positionTicks, IsPaused = isPaused };
        await connector.ReportPlaybackProgressAsync(remoteItemId, session.PlaySessionId, session.DeviceName, positionTicks, isPaused).ConfigureAwait(false);
    }

    private void HandleStop(
        IMediaServerConnector connector, string remoteItemId, string sessionKey,
        long positionTicks, bool isLinkedUser)
    {
        if (!_sessions.TryGetValue(sessionKey, out var session)) return;

        // Annuler un Stop pending précédent
        session.PendingStopCts?.Cancel();

        var stopCts        = new CancellationTokenSource();
        var capturedId     = session.PlaySessionId;   // pour la vérification anti-race
        var capturedDevice = session.DeviceName;
        // Seul le user lié au connecteur sauvegarde sa position sur le distant.
        // Les autres users ferment la session proprement mais sans écraser le resume point distant.
        var capturedPos    = isLinkedUser ? positionTicks : 0L;

        _sessions[sessionKey] = session with { PendingStopCts = stopCts };

        _ = Task.Run(async () =>
        {
            try
            {
                // Attendre 8s : si un Progress arrive, le Stop sera annulé
                await Task.Delay(TimeSpan.FromSeconds(8), stopCts.Token).ConfigureAwait(false);

                // Vérifier que la session n'a pas été remplacée par une nouvelle (Start entre-temps)
                if (_sessions.TryGetValue(sessionKey, out var current)
                    && current.PlaySessionId == capturedId
                    && _sessions.TryRemove(sessionKey, out var s))
                {
                    s.HeartbeatCts.Cancel();
                    await connector.ReportPlaybackStoppedAsync(remoteItemId, s.PlaySessionId, capturedDevice, capturedPos).ConfigureAwait(false);
                    Console.Error.WriteLine($"[VirtualLib] Stop confirmé → item={remoteItemId} pos={capturedPos} linked={isLinkedUser}");
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"[VirtualLib] Stop debounced → item={remoteItemId}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[VirtualLib] Stop error → item={remoteItemId}: {ex.GetBaseException().Message}");
            }
        });
    }

    // -------------------------------------------------------------------------
    // Heartbeat — maintient la session remote vivante toutes les 30s
    // -------------------------------------------------------------------------

    private async Task HeartbeatLoopAsync(
        IMediaServerConnector connector, string remoteItemId, string sessionKey, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !_sessions.TryGetValue(sessionKey, out var session)) break;

                await connector.ReportPlaybackProgressAsync(
                    remoteItemId, session.PlaySessionId, session.DeviceName, session.PositionTicks, session.IsPaused, ct)
                    .ConfigureAwait(false);

                Console.Error.WriteLine($"[VirtualLib] Heartbeat → item={remoteItemId} pos={session.PositionTicks} paused={session.IsPaused}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine($"[VirtualLib] Heartbeat error → item={remoteItemId}: {ex.GetBaseException().Message}"); }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private ActiveSession OpenSession(string sessionKey, long positionTicks, bool isPaused, string deviceName)
    {
        var session = new ActiveSession(Guid.NewGuid().ToString("N"), deviceName, positionTicks, isPaused, new CancellationTokenSource());
        _sessions[sessionKey] = session;
        return session;
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart    -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped  -= OnPlaybackStopped;

        foreach (var session in _sessions.Values)
        {
            session.PendingStopCts?.Cancel();
            session.HeartbeatCts.Cancel();
        }
        _sessions.Clear();

        foreach (var connector in _connectors.Values)
            connector.Dispose();
        _connectors.Clear();
    }

    private static void FireAndForget(Func<Task> action) =>
        Task.Run(action).ContinueWith(
            t => Console.Error.WriteLine($"[VirtualLib] Unhandled: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    private enum PlaybackEvent { Start, Progress, Stop }

    private sealed record ActiveSession(
        string PlaySessionId,
        string DeviceName,
        long PositionTicks,
        bool IsPaused,
        CancellationTokenSource HeartbeatCts,
        CancellationTokenSource? PendingStopCts = null);
}
