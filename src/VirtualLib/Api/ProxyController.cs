using System.IO.Pipelines;
using System.Net;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core;

namespace VirtualLib.Api;

// ---------------------------------------------------------------------------
// Request DTO
// ---------------------------------------------------------------------------

[Route("/virtuallib/proxy/{ConnectorId}/{LibraryId}/{ItemId}", "GET",
    Summary = "Pipe media stream from a remote connector to the client")]
[Unauthenticated]
public sealed class ProxyStreamRequest : IReturn<object>
{
    public string ConnectorId { get; set; } = string.Empty;
    public string LibraryId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Result: implements IHttpResult + IAsyncStreamWriter
// Emby reads StatusCode/ContentType from IHttpResult, then calls WriteToAsync
// to pipe the body into IResponse.
// ---------------------------------------------------------------------------

internal sealed class ProxyStreamResult : IHttpResult, IAsyncStreamWriter
{
    private readonly Func<IResponse, CancellationToken, Task> _writeBody;

    public ProxyStreamResult(
        System.Net.HttpStatusCode statusCode,
        string contentType,
        IRequest requestContext,
        Func<IResponse, CancellationToken, Task> writeBody)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        RequestContext = requestContext;
        _writeBody = writeBody;
    }

    // IHasHeaders (required by IHttpResult)
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // IHttpResult
    public int Status
    {
        get => (int)StatusCode;
        set => StatusCode = (System.Net.HttpStatusCode)value;
    }
    public System.Net.HttpStatusCode StatusCode { get; set; }
    public string ContentType { get; set; }
    public IRequest RequestContext { get; set; }

    // IAsyncStreamWriter
    public Task WriteToAsync(IResponse response, CancellationToken cancellationToken)
        => _writeBody(response, cancellationToken);
}

// ---------------------------------------------------------------------------
// Controller
// ---------------------------------------------------------------------------

/// <summary>
/// Transparent HTTP proxy between Emby clients and remote media servers.
///
/// Flow:
///   Client → GET /virtuallib/proxy/{connectorId}/{itemId}
///   ProxyController → GET {remoteServerUrl}/Items/{itemId}/Download (with Range)
///   Remote response piped back to client (supports seek via Range headers)
///
/// Future extension: check local cache before hitting the remote server.
/// </summary>
public sealed class ProxyController : BaseApiService
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    });

    private readonly ILogger<ProxyController> _logger;
    private readonly IConnectorFactory _connectorFactory;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public ProxyController(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager)
    {
        _logger = NullLogger<ProxyController>.Instance;
        _connectorFactory = new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance);
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    public async Task<object> Get(ProxyStreamRequest request)
    {
        var clientIp = Request.RemoteIp?.ToString() ?? "unknown";
        var range = Request.Headers["Range"];

        _logger.LogInformation(
            "Proxy request — connector={ConnectorId} item={ItemId} range={Range} from={ClientIp}",
            request.ConnectorId, request.ItemId, range ?? "none", clientIp);

        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.ConnectorId);

        if (connectorConfig is null)
        {
            _logger.LogWarning("Proxy denied — connector not found: {ConnectorId}", request.ConnectorId);
            throw new ResourceNotFoundException($"Connector '{request.ConnectorId}' not found.");
        }

        if (!connectorConfig.Enabled)
        {
            _logger.LogWarning("Proxy denied — connector disabled: {ConnectorId}", request.ConnectorId);
            throw new ResourceNotFoundException($"Connector '{request.ConnectorId}' is disabled.");
        }

        if (!connectorConfig.LibraryIds.Contains(request.LibraryId))
        {
            _logger.LogWarning("Proxy denied — library {LibraryId} not enabled on connector {ConnectorId}",
                request.LibraryId, request.ConnectorId);
            throw new ResourceNotFoundException($"Library '{request.LibraryId}' is not enabled.");
        }

        // --- Authorization check ---
        // [Unauthenticated] is required for Emby's internal ffprobe/ffmpeg pipeline.
        // We enforce our own access control:
        //   - Requests with a token: validate user has access to the virtual library folder.
        //   - Requests without a token: only allowed from private/internal network addresses
        //     (RFC 1918 + loopback), covering localhost and intra-cluster traffic (Traefik ClusterIP).
        var token = Request.Headers["X-Emby-Token"]
                 ?? Request.Headers["X-MediaBrowser-Token"]
                 ?? Request.QueryString["api_key"];

        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                var authInfo = AuthorizationContext.GetAuthorizationInfo(Request);
                if (authInfo is null || authInfo.UserId <= 0)
                {
                    Console.Error.WriteLine($"[VirtualLib] Proxy denied — invalid/expired token from {clientIp}");
                    throw new UnauthorizedAccessException("Invalid authentication token.");
                }

                var user = _userManager.GetUserById(authInfo.UserId);
                if (user == null)
                {
                    Console.Error.WriteLine($"[VirtualLib] Proxy denied — user {authInfo.UserId} not found");
                    throw new UnauthorizedAccessException("User not found.");
                }

                var libName = connectorConfig.KnownLibraries
                    .FirstOrDefault(l => l.Id == request.LibraryId)?.Name;

                if (libName == null)
                {
                    // Library ID unknown — could be a stale .strm; deny to be safe
                    Console.Error.WriteLine($"[VirtualLib] Proxy denied — libraryId {request.LibraryId} not in KnownLibraries");
                    throw new UnauthorizedAccessException($"Library '{request.LibraryId}' not recognised.");
                }

                var folderName = LibraryProvisioner.BuildFolderName(connectorConfig.DisplayName, libName);
                var virtualFolder = _libraryManager.GetVirtualFolders()
                    .FirstOrDefault(f => string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase));

                if (virtualFolder == null)
                {
                    // Virtual folder not found in Emby — deny
                    throw new UnauthorizedAccessException($"Library '{folderName}' not found.");
                }

                var hasAccess = user.Policy.EnableAllFolders
                    || (user.Policy.EnabledFolders != null
                        && user.Policy.EnabledFolders.Contains(virtualFolder.ItemId,
                            StringComparer.OrdinalIgnoreCase));

                if (!hasAccess)
                {
                    Console.Error.WriteLine(
                        $"[VirtualLib] Proxy denied — user {authInfo.UserId} has no access to '{folderName}'");
                    throw new UnauthorizedAccessException($"Access denied to library '{folderName}'.");
                }
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex)
            {
                // Fail closed: auth check exception on a token-bearing request = deny
                Console.Error.WriteLine($"[VirtualLib] Auth check failed with exception — denying: {ex.Message}");
                throw new UnauthorizedAccessException("Authorization check failed.");
            }
        }
        else if (!IsPrivateNetworkRequest(Request.RemoteIp) || !IsInternalUserAgent(Request.Headers["User-Agent"]))
        {
            var ua = Request.Headers["User-Agent"] ?? "(none)";
            Console.Error.WriteLine($"[VirtualLib] Proxy denied — unauthenticated request from {clientIp} UA={ua}");
            throw new UnauthorizedAccessException("Authentication required.");
        }

        // Connector lifetime extends into the write-body lambda (for playback reporting).
        var connector = _connectorFactory.Create(connectorConfig);
        string remoteUrl;
        try
        {
            remoteUrl = await connector.GetStreamUrlAsync(request.ItemId);
        }
        catch
        {
            connector.Dispose();
            throw;
        }

        _logger.LogDebug("Remote URL resolved: {RemoteUrl}", remoteUrl);

        // Build remote request, forwarding Range for seek support
        var remoteRequest = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
        if (!string.IsNullOrEmpty(range))
            remoteRequest.Headers.TryAddWithoutValidation("Range", range);

        // Send to remote — ResponseHeadersRead starts streaming immediately
        HttpResponseMessage remoteResponse;
        try
        {
            remoteResponse = await _http.SendAsync(remoteRequest, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            connector.Dispose();
            _logger.LogError(ex, "Upstream connection failed for item={ItemId} url={RemoteUrl}", request.ItemId, remoteUrl);
            throw new Exception($"Upstream connection failed: {ex.Message}", ex);
        }

        var statusCode = remoteResponse.StatusCode;
        var contentType = remoteResponse.Content.Headers.ContentType?.ToString()
                          ?? "application/octet-stream";
        var contentLength = remoteResponse.Content.Headers.ContentLength;

        _logger.LogInformation(
            "Upstream response — status={StatusCode} type={ContentType} length={ContentLength} item={ItemId}",
            (int)statusCode, contentType, contentLength?.ToString() ?? "unknown", request.ItemId);

        // Collect headers to forward (Content-Length, Content-Range, Accept-Ranges)
        var forwardHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (contentLength is { } cl)
            forwardHeaders["Content-Length"] = cl.ToString();

        forwardHeaders["Accept-Ranges"] = "bytes";

        // Content-Range is in Content.Headers (not response Headers) in .NET HttpClient
        var contentRangeHeader = remoteResponse.Content.Headers.ContentRange;
        if (contentRangeHeader != null)
            forwardHeaders["Content-Range"] = contentRangeHeader.ToString();

        if (forwardHeaders.TryGetValue("Content-Range", out var contentRangeValue))
            _logger.LogDebug("Content-Range forwarded: {ContentRange}", contentRangeValue);

        return new ProxyStreamResult(statusCode, contentType, Request,
            async (response, ct) =>
            {
                response.StatusCode = (int)statusCode;
                foreach (var h in forwardHeaders)
                    response.AddHeader(h.Key, h.Value);

                _logger.LogDebug("Stream start — item={ItemId} to={ClientIp}", request.ItemId, clientIp);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                using (remoteResponse)
                using (var remoteStream = await remoteResponse.Content.ReadAsStreamAsync(ct))
                {
                    var outputStream = response.OutputWriter.AsStream(leaveOpen: false);
                    try
                    {
                        await remoteStream.CopyToAsync(outputStream, ct);
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
                    {
                        // Client (ffprobe/player) closed the connection early — not our cancellation
                        _logger.LogDebug("Client closed stream early for item={ItemId}", request.ItemId);
                    }
                    catch (IOException ex) when (!ct.IsCancellationRequested)
                    {
                        // Broken pipe — client disconnected (normal for ffprobe seeking)
                        _logger.LogDebug("Broken pipe for item={ItemId}: {Message}", request.ItemId, ex.Message);
                    }
                    finally
                    {
                        try { await outputStream.DisposeAsync(); } catch { }
                    }
                }

                sw.Stop();
                _logger.LogInformation(
                    "Stream complete — item={ItemId} elapsed={ElapsedMs}ms to={ClientIp}",
                    request.ItemId, sw.ElapsedMilliseconds, clientIp);

                connector.Dispose();
            });
    }

    /// <summary>
    /// Returns true if the remote IP is a loopback or RFC 1918 private address.
    /// Used to allow unauthenticated requests from Emby's internal ffprobe/ffmpeg pipeline
    /// and intra-cluster traffic (e.g. Traefik ClusterIP on Kubernetes).
    /// </summary>
    private static bool IsPrivateNetworkRequest(IPAddress? remoteIp)
    {
        if (remoteIp is null) return true; // unknown origin — allow (server-internal)

        if (IPAddress.IsLoopback(remoteIp)) return true;

        var ip = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 10                                        // 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168);                 // 192.168.0.0/16
        }

        // IPv6 unique local (fc00::/7)
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the User-Agent looks like an internal media pipeline request
    /// (ffprobe/ffmpeg via libav, or Emby's own .NET HttpClient with no UA).
    /// Browser requests always have a full Mozilla/5.0 UA and must be authenticated.
    /// Note: UA is spoofable — this is a heuristic, not a cryptographic guarantee.
    /// </summary>
    private static bool IsInternalUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return true;  // Emby .NET HttpClient (no UA)
        return userAgent.StartsWith("Lavf/", StringComparison.OrdinalIgnoreCase);  // ffprobe/ffmpeg
    }
}
