using System.IO.Pipelines;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core;

namespace VirtualLib.Api;

// ---------------------------------------------------------------------------
// Request DTO
// ---------------------------------------------------------------------------

[Route("/virtuallib/proxy/{ConnectorId}/{ItemId}", "GET",
    Summary = "Pipe media stream from a remote connector to the client")]
[Authenticated]
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

    private static readonly Lazy<IConnectorFactory> _connectorFactory =
        new(() => new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance));

    public async Task<object> Get(ProxyStreamRequest request)
    {
        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.ConnectorId);

        if (connectorConfig is null)
            throw new ResourceNotFoundException($"Connector '{request.ConnectorId}' not found.");

        // Get remote stream URL from connector (handles Emby/Jellyfin/Plex differences)
        string remoteUrl;
        using (var connector = _connectorFactory.Value.Create(connectorConfig))
            remoteUrl = await connector.GetStreamUrlAsync(request.ItemId);

        // Build remote request, forwarding Range for seek support
        var remoteRequest = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
        var rangeHeader = Request.Headers["Range"];
        if (!string.IsNullOrEmpty(rangeHeader))
            remoteRequest.Headers.TryAddWithoutValidation("Range", rangeHeader);

        // Send to remote — ResponseHeadersRead starts streaming immediately
        HttpResponseMessage remoteResponse;
        try
        {
            remoteResponse = await _http.SendAsync(remoteRequest, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            throw new Exception($"Upstream connection failed: {ex.Message}", ex);
        }

        var statusCode = remoteResponse.StatusCode;
        var contentType = remoteResponse.Content.Headers.ContentType?.ToString()
                          ?? "application/octet-stream";

        // Collect headers to forward (Content-Length, Content-Range, Accept-Ranges)
        var forwardHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (remoteResponse.Content.Headers.ContentLength is { } cl)
            forwardHeaders["Content-Length"] = cl.ToString();

        forwardHeaders["Accept-Ranges"] = "bytes";

        foreach (var h in remoteResponse.Headers)
        {
            if (h.Key.Equals("Content-Range", StringComparison.OrdinalIgnoreCase))
                forwardHeaders["Content-Range"] = string.Join(", ", h.Value);
        }

        return new ProxyStreamResult(statusCode, contentType, Request,
            async (response, ct) =>
            {
                // Set extra headers on the IResponse before writing body
                response.StatusCode = (int)statusCode;
                foreach (var h in forwardHeaders)
                    response.AddHeader(h.Key, h.Value);

                using (remoteResponse)
                using (var remoteStream = await remoteResponse.Content.ReadAsStreamAsync(ct))
                await using (var outputStream = response.OutputWriter.AsStream(leaveOpen: false))
                    await remoteStream.CopyToAsync(outputStream, ct);
            });
    }
}
