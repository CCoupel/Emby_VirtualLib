using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtualLib.Connectors.Internal;
using VirtualLib.Core;
using VirtualLib.Core.Models;

namespace VirtualLib.Connectors;

/// <summary>
/// Connecteur Plex via plex.tv : authentifie sur plex.tv, résout automatiquement
/// la meilleure URL de connexion (local → plex.direct → relay), puis délègue
/// toutes les opérations média à un PlexConnector interne.
/// </summary>
public sealed class PlexTvConnector : IMediaServerConnector
{
    private const string PlexTvBase = "https://plex.tv";

    private readonly ConnectorConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PlexTvConnector> _logger;

    private PlexConnector? _inner;
    private string? _resolvedToken;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;

    public string ServerType  => ServerTypes.PlexTV;
    public string ConnectorId => _config.Id;
    public string DisplayName => _config.DisplayName;

    public PlexTvConnector(
        ConnectorConfig config,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<PlexTvConnector> logger)
    {
        _config          = config;
        _httpClientFactory = httpClientFactory;
        _loggerFactory   = loggerFactory;
        _logger          = logger;
    }

    // -------------------------------------------------------------------------
    // Inner PlexConnector — lazy, initialised on first use
    // -------------------------------------------------------------------------

    private async Task<PlexConnector> GetInnerAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_inner is not null) return _inner;

            var token = await GetTokenAsync(ct);
            var (serverUrl, serverToken) = await ResolveServerUrlAsync(token, ct);

            // Prefer the per-server access token (works for shared servers);
            // fall back to the global auth token if plex.tv didn't return one.
            var pmsToken = !string.IsNullOrEmpty(serverToken) ? serverToken : token;

            var innerConfig = new ConnectorConfig
            {
                Id             = _config.Id,
                DisplayName    = _config.DisplayName,
                ServerType     = ServerTypes.Plex,
                ServerUrl      = serverUrl,
                AuthMode       = AuthMode.ApiKey,
                ApiKey         = pmsToken,
                MetadataMode   = _config.MetadataMode,
                LibraryIds     = _config.LibraryIds,
                Enabled        = _config.Enabled,
                KnownLibraries = _config.KnownLibraries
            };

            _inner = new PlexConnector(innerConfig, _httpClientFactory, _loggerFactory.CreateLogger<PlexConnector>());
            return _inner;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Token resolution
    // -------------------------------------------------------------------------

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_resolvedToken is not null) return _resolvedToken;

        _resolvedToken = _config.AuthMode == AuthMode.ApiKey
            ? _config.ApiKey
            : await GetTokenFromCredentialsAsync(_config.Username, _config.Password, _httpClientFactory, ct);

        return _resolvedToken;
    }

    /// <summary>
    /// Authenticates on plex.tv and returns the authToken.
    /// Pass <paramref name="twoFactorPin"/> (6-digit TOTP code) when the account has 2FA enabled.
    /// </summary>
    internal static async Task<string> GetTokenFromCredentialsAsync(
        string username, string password, IHttpClientFactory httpClientFactory, CancellationToken ct,
        string? twoFactorPin = null)
    {
        using var client = CreatePlexTvClient(httpClientFactory);

        // Plex 2FA: append the TOTP code directly to the password (no separator).
        // This is the documented approach — a separate field is not supported.
        var effectivePassword = string.IsNullOrWhiteSpace(twoFactorPin)
            ? password
            : password + twoFactorPin.Trim();

        var formFields = new List<KeyValuePair<string, string>>
        {
            new("user[login]",    username),
            new("user[password]", effectivePassword)
        };

        var form = new FormUrlEncodedContent(formFields);

        using var response = await client.PostAsync($"{PlexTvBase}/users/sign_in.json", form, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var hint = body.Contains("two_factor", StringComparison.OrdinalIgnoreCase)
                       || string.IsNullOrWhiteSpace(twoFactorPin)
                ? " Your account has 2FA enabled — enter your 6-digit TOTP code in the '2FA Code' field."
                : string.Empty;
            throw new InvalidOperationException("Authentication failed (401)." + hint);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PlexTvSignInResponse>(
            cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty sign_in response from plex.tv");

        return string.IsNullOrEmpty(result.User?.AuthToken)
            ? throw new InvalidOperationException("Empty authToken from plex.tv")
            : result.User.AuthToken;
    }

    // -------------------------------------------------------------------------
    // Server URL resolution
    // -------------------------------------------------------------------------

    private async Task<(string Url, string AccessToken)> ResolveServerUrlAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.PlexMachineIdentifier))
            throw new InvalidOperationException("PlexMachineIdentifier is not configured. Select a server in the connector settings.");

        var servers = await FetchResourcesAsync(token, ct);
        var server  = servers.FirstOrDefault(r => r.ClientIdentifier == _config.PlexMachineIdentifier)
                      ?? throw new InvalidOperationException(
                          $"Server '{_config.PlexMachineIdentifier}' not found in plex.tv resources.");

        var best = PickBestConnection(server.Connections)
                   ?? throw new InvalidOperationException(
                       $"No usable connection found for Plex server '{server.Name}'.");

        _logger.LogInformation(
            "Resolved PlexTV server '{Name}' → {Uri} (local={Local}, relay={Relay}, hasServerToken={HasToken})",
            server.Name, best.Uri, best.Local, best.Relay, !string.IsNullOrEmpty(server.AccessToken));

        return (best.Uri, server.AccessToken);
    }

    private static PlexTvConnection? PickBestConnection(List<PlexTvConnection> connections) =>
        connections
            .Where(c => !c.IPv6 && !c.Local && !string.IsNullOrEmpty(c.Uri))  // exclude LAN-local (unreachable from remote)
            .OrderByDescending(c => c.Relay  ? 0 : 1)   // prefer plex.direct over relay
            .ThenByDescending(c => c.Protocol == "https" ? 1 : 0)
            .FirstOrDefault()
        ?? connections  // fallback: try local if no remote connection available
            .Where(c => !c.IPv6 && !string.IsNullOrEmpty(c.Uri))
            .OrderByDescending(c => c.Relay  ? 0 : 1)
            .ThenByDescending(c => c.Protocol == "https" ? 1 : 0)
            .FirstOrDefault();

    // -------------------------------------------------------------------------
    // Static helpers (used by ConfigController for the server picker endpoint)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lists all Plex Media Server resources visible to the given token on plex.tv.
    /// Returns servers with their best resolved connection URL.
    /// </summary>
    internal static async Task<List<PlexServerInfo>> ListServersAsync(
        string token, IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        var resources = await FetchResourcesAsync(token, httpClientFactory, ct);

        return resources
            .Select(r =>
            {
                var best = PickBestConnection(r.Connections);
                return new PlexServerInfo
                {
                    Name              = r.Name,
                    MachineIdentifier = r.ClientIdentifier,
                    ResolvedUrl       = best?.Uri ?? string.Empty,
                    IsLocal           = best?.Local ?? false,
                    IsRelay           = best?.Relay ?? false
                };
            })
            .Where(s => !string.IsNullOrEmpty(s.ResolvedUrl))
            .ToList();
    }

    private async Task<List<PlexTvResource>> FetchResourcesAsync(string token, CancellationToken ct)
    {
        using var client = CreatePlexTvClient(_httpClientFactory);
        return await FetchResourcesCore(client, token, ct);
    }

    private static async Task<List<PlexTvResource>> FetchResourcesAsync(
        string token, IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        using var client = CreatePlexTvClient(httpClientFactory);
        return await FetchResourcesCore(client, token, ct);
    }

    private static async Task<List<PlexTvResource>> FetchResourcesCore(
        HttpClient client, string token, CancellationToken ct)
    {
        var url = $"{PlexTvBase}/api/v2/resources?includeHttps=1&includeRelay=1&X-Plex-Token={token}";
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var resources = await response.Content.ReadFromJsonAsync<List<PlexTvResource>>(
            cancellationToken: ct) ?? new List<PlexTvResource>();

        return resources
            .Where(r => r.Provides.Contains("server", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static HttpClient CreatePlexTvClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Plex-Product",          "VirtualLib");
        client.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "virtuallib-plugin");
        client.DefaultRequestHeaders.Add("X-Plex-Version",           "1.0.0");
        client.DefaultRequestHeaders.Add("Accept",                   "application/json");
        return client;
    }

    // -------------------------------------------------------------------------
    // IMediaServerConnector — delegate to inner PlexConnector
    // -------------------------------------------------------------------------

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var inner = await GetInnerAsync(cancellationToken);
            return await inner.TestConnectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlexTV connection test failed for connector {ConnectorId}", ConnectorId);
            return ConnectorTestResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<RemoteLibrary>> ListLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        return await inner.ListLibrariesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MediaItem>> ListItemsAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        return await inner.ListItemsAsync(libraryId, cancellationToken);
    }

    public async Task<int> GetItemCountAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        return await inner.GetItemCountAsync(libraryId, cancellationToken);
    }

    public async Task<MediaMetadata> GetMetadataAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        return await inner.GetMetadataAsync(itemId, cancellationToken);
    }

    public async Task<string> GetStreamUrlAsync(string itemId, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        return await inner.GetStreamUrlAsync(itemId, cancellationToken);
    }

    public async Task<string> DownloadFileToPathAsync(string itemId, string destPathNoExt, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        return await inner.DownloadFileToPathAsync(itemId, destPathNoExt, cancellationToken);
    }

    public async Task<Stream?> GetArtworkStreamAsync(string itemId, ArtworkType artworkType, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        return await inner.GetArtworkStreamAsync(itemId, artworkType, cancellationToken);
    }

    public async Task ReportPlaybackStartAsync(string itemId, string playSessionId, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        await inner.ReportPlaybackStartAsync(itemId, playSessionId, cancellationToken);
    }

    public async Task ReportPlaybackProgressAsync(string itemId, string playSessionId, long positionTicks, bool isPaused, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        await inner.ReportPlaybackProgressAsync(itemId, playSessionId, positionTicks, isPaused, cancellationToken);
    }

    public async Task ReportPlaybackStoppedAsync(string itemId, string playSessionId, long positionTicks, CancellationToken cancellationToken = default)
    {
        var inner = await GetInnerAsync(cancellationToken);
        await inner.ReportPlaybackStoppedAsync(itemId, playSessionId, positionTicks, cancellationToken);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _inner?.Dispose();
            _initLock.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>Plex server visible on plex.tv, with its best resolved connection URL.</summary>
public sealed class PlexServerInfo
{
    public string Name              { get; set; } = string.Empty;
    public string MachineIdentifier { get; set; } = string.Empty;
    public string ResolvedUrl       { get; set; } = string.Empty;
    public bool   IsLocal           { get; set; }
    public bool   IsRelay           { get; set; }
}
