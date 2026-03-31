using System.IO.Pipelines;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core;

namespace VirtualLib.Api;

// ---------------------------------------------------------------------------
// Request DTO
// ---------------------------------------------------------------------------

[Route("/virtuallib/proxy/{ConnectorId}/{ItemId}", "GET",
    Summary = "Pipe media stream from a remote connector to the client")]
[Unauthenticated]
public sealed class ProxyStreamRequest : IReturn<object>
{
    public string ConnectorId { get; set; } = string.Empty;
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

    public ProxyController()
    {
        _logger = NullLoggerFactory.Instance.CreateLogger<ProxyController>();
        _connectorFactory = new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance);
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
            _logger.LogWarning("Connector not found: {ConnectorId}", request.ConnectorId);
            throw new ResourceNotFoundException($"Connector '{request.ConnectorId}' not found.");
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

                // Notify the remote server that playback has started (user credentials mode only)
                await connector.ReportPlaybackStartAsync(request.ItemId, ct);

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

                // Notify the remote server that playback has stopped
                await connector.ReportPlaybackStoppedAsync(request.ItemId, CancellationToken.None);
                connector.Dispose();
            });
    }
}
