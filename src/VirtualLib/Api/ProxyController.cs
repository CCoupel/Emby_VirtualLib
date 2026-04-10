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
using VirtualLib.Core.Cache;

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
/// Flow (cache disabled or miss):
///   Client → GET /virtuallib/proxy/{connectorId}/{itemId}
///   ProxyController → GET {remoteServerUrl}/Items/{itemId}/Download (with Range)
///   Remote response piped back to client + written to local chunk cache
///
/// Flow (cache hit — all requested chunks present):
///   Client → ProxyController → reads directly from disk, no source contact
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

        // ── Cache: serve entirely from disk if all requested chunks are present ──
        var cacheEnabled = config.CacheEnabled && connectorConfig.CacheEnabled && Plugin.Cache != null;

        if (cacheEnabled)
        {
            await Plugin.Cache!.ValidateItemAsync(connectorConfig.Id, request.ItemId);
            var manifest = await Plugin.Cache!.GetManifestAsync(connectorConfig.Id, request.ItemId);
            // Reject manifests with a suspiciously small TotalSize — they were likely created from
            // an error response (Plex returning a few hundred bytes with 200 OK) and must be
            // re-fetched from the source so the real file size can be recorded.
            if (manifest?.TotalSize > 0 && manifest.TotalSize < 1024)
            {
                Console.Error.WriteLine($"[VirtualLib] Proxy cache manifest invalid TotalSize={manifest.TotalSize} for item={request.ItemId} — purging");
                await Plugin.Cache.InvalidateAsync(connectorConfig.Id, request.ItemId);
                manifest = null;
            }

            if (manifest?.TotalSize > 0)
            {
                var (cachedStart, cachedEnd) = ParseRangeHeader(range, manifest.TotalSize);
                if (Plugin.Cache.IsRangeCached(manifest, cachedStart, cachedEnd))
                {
                    _logger.LogInformation(
                        "Cache hit — item={ItemId} range={Start}-{End} (serving from disk)",
                        request.ItemId, cachedStart, cachedEnd);

                    var cachedLength  = cachedEnd - cachedStart + 1;
                    var cachedStatus  = string.IsNullOrEmpty(range)
                        ? HttpStatusCode.OK : HttpStatusCode.PartialContent;
                    var cachedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Content-Length"]  = cachedLength.ToString(),
                        ["Accept-Ranges"]   = "bytes",
                    };
                    if (!string.IsNullOrEmpty(range))
                        cachedHeaders["Content-Range"] = $"bytes {cachedStart}-{cachedEnd}/{manifest.TotalSize}";

                    // Connector not needed for a cache hit (not yet created)
                    return new ProxyStreamResult(cachedStatus, manifest.ContentType, Request,
                        async (response, ct) =>
                        {
                            response.StatusCode = (int)cachedStatus;
                            foreach (var h in cachedHeaders) response.AddHeader(h.Key, h.Value);

                            var output = response.OutputWriter.AsStream(leaveOpen: false);
                            try
                            {
                                await Plugin.Cache.ServeCachedRangeAsync(
                                    manifest, connectorConfig.Id, request.ItemId,
                                    cachedStart, cachedEnd, output, ct);
                            }
                            catch (OperationCanceledException) { }
                            catch (IOException ex) when (!ct.IsCancellationRequested)
                            {
                                _logger.LogDebug("Broken pipe (cache) item={ItemId}: {Msg}", request.ItemId, ex.Message);
                            }
                            finally { try { await output.DisposeAsync(); } catch { } }

                            _ = Plugin.Cache?.ValidateItemAsync(connectorConfig.Id, request.ItemId, CancellationToken.None);
                        });
                }
            }
        }

        // ── Proxy path: fetch from source (+ write-through to cache if enabled) ──

        // ── Pending segment: wait if another client is already downloading this range ──
        if (cacheEnabled && Plugin.Cache != null)
        {
            var chunkSize    = config.CacheChunkSizeMb * 1024 * 1024;
            var rangeStart   = ParseRangeStart(range);
            var alignedStart = rangeStart / chunkSize * chunkSize;

            var committed = await Plugin.Cache.WaitForPendingSegmentAsync(
                connectorConfig.Id, request.ItemId, alignedStart, CancellationToken.None);

            if (committed)
            {
                var freshManifest = await Plugin.Cache.GetManifestAsync(connectorConfig.Id, request.ItemId);
                if (freshManifest?.TotalSize > 0 && freshManifest.TotalSize < 1024)
                {
                    Console.Error.WriteLine($"[VirtualLib] Proxy pending manifest invalid TotalSize={freshManifest.TotalSize} for item={request.ItemId} — purging");
                    await Plugin.Cache.InvalidateAsync(connectorConfig.Id, request.ItemId);
                    freshManifest = null;
                }

                if (freshManifest?.TotalSize > 0)
                {
                    var (ps, pe) = ParseRangeHeader(range, freshManifest.TotalSize);
                    if (Plugin.Cache.IsRangeCached(freshManifest, ps, pe))
                    {
                        _logger.LogInformation(
                            "Pending hit — item={ItemId} range={Start}-{End} (served after wait)",
                            request.ItemId, ps, pe);

                        var pLen    = pe - ps + 1;
                        var pStatus = string.IsNullOrEmpty(range) ? HttpStatusCode.OK : HttpStatusCode.PartialContent;
                        var pHdrs   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Content-Length"] = pLen.ToString(),
                            ["Accept-Ranges"]  = "bytes",
                        };
                        if (!string.IsNullOrEmpty(range))
                            pHdrs["Content-Range"] = $"bytes {ps}-{pe}/{freshManifest.TotalSize}";

                        return new ProxyStreamResult(pStatus, freshManifest.ContentType, Request,
                            async (response, innerCt) =>
                            {
                                response.StatusCode = (int)pStatus;
                                foreach (var h in pHdrs) response.AddHeader(h.Key, h.Value);
                                var output = response.OutputWriter.AsStream(leaveOpen: false);
                                try
                                {
                                    await Plugin.Cache.ServeCachedRangeAsync(
                                        freshManifest, connectorConfig.Id, request.ItemId,
                                        ps, pe, output, innerCt);
                                }
                                catch (OperationCanceledException) { }
                                catch (IOException ex) when (!innerCt.IsCancellationRequested)
                                {
                                    _logger.LogDebug("Broken pipe (pending→cache) item={ItemId}: {Msg}", request.ItemId, ex.Message);
                                }
                                finally { try { await output.DisposeAsync(); } catch { } }
                                _ = Plugin.Cache?.ValidateItemAsync(connectorConfig.Id, request.ItemId, CancellationToken.None);
                            });
                    }
                }
            }
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

        Console.Error.WriteLine($"[VirtualLib] Proxy remote URL item={request.ItemId}: {remoteUrl}");

        var remoteRequest = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
        if (!string.IsNullOrEmpty(range))
            remoteRequest.Headers.TryAddWithoutValidation("Range", range);

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

        var statusCode   = remoteResponse.StatusCode;
        var contentType  = remoteResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var contentLength = remoteResponse.Content.Headers.ContentLength;

        // For 206 Partial Content, Content-Length is the range size, not the total file size.
        // Use Content-Range "total" field to get the actual file size for the cache manifest.
        var contentRangeHeader = remoteResponse.Content.Headers.ContentRange;
        var manifestTotalSize = statusCode == HttpStatusCode.PartialContent
                                && contentRangeHeader?.Length is { } rangeTotal
            ? rangeTotal
            : contentLength;

        _logger.LogInformation(
            "Upstream response — status={StatusCode} type={ContentType} length={ContentLength} totalSize={TotalSize} item={ItemId}",
            (int)statusCode, contentType, contentLength?.ToString() ?? "unknown",
            manifestTotalSize?.ToString() ?? "unknown", request.ItemId);

        // Reject non-media upstream responses (Plex error pages in 200 OK, XML errors, etc.).
        // If the content type is clearly not binary/video/audio, ffmpeg would receive garbage and
        // fail with "EBML header parsing failed" or similar demuxer errors.
        if (!IsMediaContentType(contentType))
        {
            connector.Dispose();
            remoteResponse.Dispose();
            Console.Error.WriteLine($"[VirtualLib] Proxy non-media Content-Type '{contentType}' for item={request.ItemId} url={remoteUrl}");
            throw new Exception($"Upstream Content-Type '{contentType}' for item {request.ItemId}.");
        }

        // Guard: if upstream returned a suspiciously small response, it's likely an error page
        // that slipped through with octet-stream content type.
        const long MinValidMediaBytes = 1024;
        if (manifestTotalSize is { } sz && sz < MinValidMediaBytes)
        {
            _logger.LogWarning(
                "Upstream response is too small ({Size} bytes) for item={ItemId} — likely an error page, skipping cache",
                sz, request.ItemId);
            manifestTotalSize = null; // prevent caching a bad manifest
        }

        // Initialise cache manifest from upstream headers (idempotent)
        ChunkManifest? cacheManifest = null;
        if (cacheEnabled && manifestTotalSize is { } totalLen && totalLen > 0)
        {
            cacheManifest = await Plugin.Cache!.EnsureManifestAsync(
                connectorConfig.Id, request.ItemId,
                totalLen, contentType, remoteUrl,
                config.CacheChunkSizeMb * 1024 * 1024);
        }

        var forwardHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (contentLength is { } cl) forwardHeaders["Content-Length"] = cl.ToString();
        forwardHeaders["Accept-Ranges"] = "bytes";

        // Content-Range is in Content.Headers (not response.Headers) in .NET HttpClient
        if (contentRangeHeader != null)
            forwardHeaders["Content-Range"] = contentRangeHeader.ToString();

        if (forwardHeaders.TryGetValue("Content-Range", out var contentRangeValue))
            _logger.LogDebug("Content-Range forwarded: {ContentRange}", contentRangeValue);

        // Capture range start for write-through offset tracking
        var proxyRangeStart = cacheManifest != null
            ? ParseRangeHeader(range, cacheManifest.TotalSize).Start
            : 0L;

        return new ProxyStreamResult(statusCode, contentType, Request,
            async (response, ct) =>
            {
                response.StatusCode = (int)statusCode;
                foreach (var h in forwardHeaders) response.AddHeader(h.Key, h.Value);

                _logger.LogDebug("Stream start — item={ItemId} to={ClientIp}", request.ItemId, clientIp);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                using (remoteResponse)
                using (var remoteStream = await remoteResponse.Content.ReadAsStreamAsync(ct))
                {
                    var outputStream = response.OutputWriter.AsStream(leaveOpen: false);
                    try
                    {
                        if (cacheManifest != null)
                        {
                            // Write-through: stream to client AND populate cache
                            await Plugin.Cache!.CopyWithCacheAsync(
                                remoteStream, outputStream, cacheManifest,
                                connectorConfig.Id, request.ItemId, proxyRangeStart,
                                config.CacheChunkSizeMb * 1024 * 1024,
                                config.CacheCompletionThresholdPercent,
                                ct);
                        }
                        else
                        {
                            await remoteStream.CopyToAsync(outputStream, ct);
                        }
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
                    {
                        _logger.LogDebug("Client closed stream early for item={ItemId}", request.ItemId);
                    }
                    catch (IOException ex) when (!ct.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"[VirtualLib] Proxy upstream closed early for item={request.ItemId}: {ex.Message}");
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

                if (cacheManifest != null)
                    _ = Plugin.Cache?.ValidateItemAsync(connectorConfig.Id, request.ItemId, CancellationToken.None);

                connector.Dispose();
            });
    }

    /// <summary>Returns the start byte from a Range header, or 0 if absent/unparseable.</summary>
    private static long ParseRangeStart(string? rangeHeader)
    {
        if (!string.IsNullOrEmpty(rangeHeader) &&
            rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader["bytes=".Length..];
            var dash = spec.IndexOf('-');
            if (dash > 0 && long.TryParse(spec[..dash], out var start))
                return start;
        }
        return 0L;
    }

    /// <summary>
    /// Parses a Range header (e.g. "bytes=0-1048575") into (start, end).
    /// Returns (0, totalSize-1) for missing or open-ended ranges.
    /// </summary>
    private static (long Start, long End) ParseRangeHeader(string? rangeHeader, long totalSize)
    {
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec  = rangeHeader.Substring("bytes=".Length);
            var dash  = spec.IndexOf('-');
            if (dash >= 0)
            {
                var startStr = spec[..dash];
                var endStr   = spec[(dash + 1)..];
                var start = string.IsNullOrEmpty(startStr) ? 0L : long.Parse(startStr);
                var end   = string.IsNullOrEmpty(endStr)   ? (totalSize > 0 ? totalSize - 1 : long.MaxValue)
                                                           : long.Parse(endStr);
                return (start, end);
            }
        }
        return (0L, totalSize > 0 ? totalSize - 1 : long.MaxValue);
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
    /// Returns true if the Content-Type indicates binary/video/audio data.
    /// Rejects text/html, text/xml, application/xml and similar error-page types that
    /// Plex (and other servers) sometimes return as 200 OK when a file is unavailable.
    /// </summary>
    private static bool IsMediaContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return true; // unknown → let it through

        var ct = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return ct.StartsWith("video/")
            || ct.StartsWith("audio/")
            || ct == "application/octet-stream"
            || ct == "application/x-matroska"
            || ct == "application/mp4"
            || ct == "application/ogg"
            || ct == "application/vnd.rn-realmedia"
            || ct == "binary/octet-stream";
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
