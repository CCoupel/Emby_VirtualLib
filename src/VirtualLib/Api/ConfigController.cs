using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core;
using VirtualLib.Core.Models;

namespace VirtualLib.Api;

// ---------------------------------------------------------------------------
// Request DTOs — each is decorated with [Route] and [Authenticated]
// ---------------------------------------------------------------------------

[Route("/virtuallib/connectors", "GET", Summary = "List all configured connectors")]
[Authenticated]
public sealed class GetConnectors : IReturn<List<ConnectorConfig>> { }

[Route("/virtuallib/connectors", "POST", Summary = "Add a new connector")]
[Authenticated]
public sealed class CreateConnector : IReturn<ConnectorConfig>
{
    public string DisplayName { get; set; } = string.Empty;
    public string ServerType { get; set; } = ServerTypes.Emby;
    public string ServerUrl { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.ApiKey;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public List<string> LibraryIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

[Route("/virtuallib/connectors/{Id}", "PUT", Summary = "Update an existing connector")]
[Authenticated]
public sealed class UpdateConnector : IReturn<ConnectorConfig>
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ServerType { get; set; } = ServerTypes.Emby;
    public string ServerUrl { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.ApiKey;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public List<string> LibraryIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

[Route("/virtuallib/connectors/{Id}", "DELETE", Summary = "Remove a connector")]
[Authenticated]
public sealed class DeleteConnector : IReturnVoid
{
    public string Id { get; set; } = string.Empty;
}

[Route("/virtuallib/connectors/{Id}/test", "POST", Summary = "Test connector connectivity")]
[Authenticated]
public sealed class TestConnector : IReturn<ConnectorTestResult>
{
    public string Id { get; set; } = string.Empty;
}

[Route("/virtuallib/connectors/{Id}/libraries", "GET", Summary = "List remote libraries available on a connector")]
[Authenticated]
public sealed class GetConnectorLibraries : IReturn<List<RemoteLibrary>>
{
    public string Id { get; set; } = string.Empty;
}

[Route("/virtuallib/connectors/{Id}/stats", "GET", Summary = "Get entry counts per library")]
[Authenticated]
public sealed class GetConnectorStats : IReturn<List<LibraryStats>>
{
    public string Id { get; set; } = string.Empty;
}

[Route("/virtuallib/connectors/{Id}/libraries/{LibraryId}/sync", "POST", Summary = "Sync a single library")]
[Authenticated]
public sealed class SyncLibrary : IReturn<SyncResult>
{
    public string Id { get; set; } = string.Empty;
    public string LibraryId { get; set; } = string.Empty;
}

[Route("/virtuallib/settings", "GET", Summary = "Get global settings")]
[Authenticated]
public sealed class GetSettings : IReturn<GlobalSettings> { }

[Route("/virtuallib/settings", "PUT", Summary = "Save global settings")]
[Authenticated]
public sealed class SaveSettings : IReturn<GlobalSettings>
{
    public string VirtualLibraryRootPath { get; set; } = string.Empty;
    public string ProxyBaseUrl { get; set; } = string.Empty;
    public int SyncIntervalHours { get; set; } = 6;
    public int ProxyTimeoutSeconds { get; set; } = 30;
}

[Route("/virtuallib/sync", "POST", Summary = "Sync all enabled connectors")]
[Authenticated]
public sealed class SyncAll : IReturn<List<SyncResult>> { }

[Route("/virtuallib/connectors/{Id}/sync", "POST", Summary = "Sync a single connector")]
[Authenticated]
public sealed class SyncConnector : IReturn<SyncResult>
{
    public string Id { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------

public sealed class GlobalSettings
{
    public string VirtualLibraryRootPath { get; set; } = string.Empty;
    public string ProxyBaseUrl { get; set; } = string.Empty;
    public int SyncIntervalHours { get; set; } = 6;
    public int ProxyTimeoutSeconds { get; set; } = 30;
}

public sealed class LibraryStats
{
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public int EntryCount { get; set; }
}

// ---------------------------------------------------------------------------
// Service implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Handles all /virtuallib/* API endpoints for managing connectors and
/// triggering synchronisation.
/// </summary>
public sealed class ConfigController : BaseApiService
{
    private static readonly Dictionary<string, string>? NoHeaders = null;

    // Services instanciés une seule fois par classe (singleton léger via lazy)
    private static readonly Lazy<IConnectorFactory> _connectorFactory =
        new(() => new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance));

    private static readonly Lazy<SyncService> _syncService =
        new(() => new SyncService(
            _connectorFactory.Value,
            new StrmGenerator(),
            new NfoGenerator(),
            NullLogger<SyncService>.Instance));

    private readonly LibraryProvisioner _libraryProvisioner;

    public ConfigController(ILibraryManager libraryManager)
    {
        _libraryProvisioner = new LibraryProvisioner(libraryManager, NullLogger<LibraryProvisioner>.Instance);
    }

    /// <summary>
    /// Base URL for .strm proxy links.
    /// Uses the value from plugin configuration if set; otherwise auto-detects
    /// from the incoming request (with X-Forwarded-Proto/Host support).
    /// </summary>
    private string ProxyBaseUrl
    {
        get
        {
            // Explicit config takes priority
            var configured = Plugin.Instance?.Configuration.ProxyBaseUrl?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(configured))
                return configured;

            // Auto-detect from request
            try
            {
                var raw = Request?.AbsoluteUri;
                if (!string.IsNullOrEmpty(raw))
                {
                    var idx = raw.IndexOf("/virtuallib/", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        var baseUrl = raw.Substring(0, idx);
                        var uri = new Uri(baseUrl);

                        var forwardedProto = Request?.Headers["X-Forwarded-Proto"]?.Split(',')[0].Trim();
                        var forwardedHost  = Request?.Headers["X-Forwarded-Host"]?.Split(',')[0].Trim();
                        var forwardedPort  = Request?.Headers["X-Forwarded-Port"]?.Split(',')[0].Trim();
                        var forwardedSsl   = Request?.Headers["X-Forwarded-Ssl"]?.Split(',')[0].Trim();

                        string scheme;
                        if (!string.IsNullOrEmpty(forwardedProto))
                            scheme = forwardedProto;
                        else if (string.Equals(forwardedSsl, "on", StringComparison.OrdinalIgnoreCase))
                            scheme = "https";
                        else if (forwardedPort == "443")
                            scheme = "https";
                        else
                            scheme = uri.Scheme;

                        var authority = !string.IsNullOrEmpty(forwardedHost) ? forwardedHost : uri.Authority;
                        var path = uri.AbsolutePath.TrimEnd('/');

                        return $"{scheme}://{authority}{path}";
                    }
                }
            }
            catch { }
            return "http://localhost:8096";
        }
    }

    // -----------------------------------------------------------------------
    // GET /virtuallib/connectors
    // -----------------------------------------------------------------------
    public object Get(GetConnectors request)
    {
        var config = Plugin.Instance!.Configuration;
        return ResultFactory.GetResult(Request, config.Connectors, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // POST /virtuallib/connectors
    // -----------------------------------------------------------------------
    public object Post(CreateConnector request)
    {
        var config = Plugin.Instance!.Configuration;

        var connector = new ConnectorConfig
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = request.DisplayName,
            ServerType = request.ServerType,
            ServerUrl = request.ServerUrl,
            AuthMode = request.AuthMode,
            ApiKey = request.ApiKey,
            Username = request.Username,
            Password = request.Password,
            LibraryIds = request.LibraryIds,
            Enabled = request.Enabled
        };

        config.Connectors.Add(connector);
        Plugin.Instance.SaveConfiguration();

        return ResultFactory.GetResult(Request, connector, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // PUT /virtuallib/connectors/{Id}
    // -----------------------------------------------------------------------
    public object Put(UpdateConnector request)
    {
        var config = Plugin.Instance!.Configuration;
        var existing = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (existing is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        config.Connectors.Remove(existing);

        var updated = new ConnectorConfig
        {
            Id = existing.Id,
            DisplayName = request.DisplayName,
            ServerType = request.ServerType,
            ServerUrl = request.ServerUrl,
            AuthMode = request.AuthMode,
            ApiKey = request.ApiKey,
            Username = request.Username,
            // Preserve existing password if the client sent an empty string (placeholder pattern)
            Password = string.IsNullOrEmpty(request.Password) ? existing.Password : request.Password,
            LibraryIds = request.LibraryIds,
            Enabled = request.Enabled,
            KnownLibraries = existing.KnownLibraries
        };

        config.Connectors.Add(updated);
        Plugin.Instance.SaveConfiguration();

        // Sync virtual folders: create for newly checked, remove for unchecked
        var virtualLibRoot = config.VirtualLibraryRootPath;
        if (!string.IsNullOrEmpty(virtualLibRoot))
        {
            var addedIds   = request.LibraryIds.Except(existing.LibraryIds).ToList();
            var removedIds = existing.LibraryIds.Except(request.LibraryIds).ToList();

            foreach (var lib in updated.KnownLibraries.Where(l => addedIds.Contains(l.Id)))
                _libraryProvisioner.EnsureVirtualFolder(updated.DisplayName, lib.Name, lib.Type, virtualLibRoot);

            foreach (var lib in existing.KnownLibraries.Where(l => removedIds.Contains(l.Id)))
                _libraryProvisioner.RemoveVirtualFolder(existing.DisplayName, lib.Name);
        }

        return ResultFactory.GetResult(Request, updated, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // DELETE /virtuallib/connectors/{Id}
    // -----------------------------------------------------------------------
    public void Delete(DeleteConnector request)
    {
        var config = Plugin.Instance!.Configuration;
        var existing = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (existing is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        config.Connectors.Remove(existing);
        Plugin.Instance.SaveConfiguration();

        // Remove all virtual folders for this connector's libraries
        foreach (var lib in existing.KnownLibraries)
            _libraryProvisioner.RemoveVirtualFolder(existing.DisplayName, lib.Name);
    }

    // -----------------------------------------------------------------------
    // GET /virtuallib/settings
    // -----------------------------------------------------------------------
    public object Get(GetSettings request)
    {
        var config = Plugin.Instance!.Configuration;
        return ResultFactory.GetResult(Request, new GlobalSettings
        {
            VirtualLibraryRootPath = config.VirtualLibraryRootPath,
            ProxyBaseUrl = config.ProxyBaseUrl,
            SyncIntervalHours = config.SyncIntervalHours,
            ProxyTimeoutSeconds = config.ProxyTimeoutSeconds
        }, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // PUT /virtuallib/settings
    // -----------------------------------------------------------------------
    public object Put(SaveSettings request)
    {
        var config = Plugin.Instance!.Configuration;
        config.VirtualLibraryRootPath = request.VirtualLibraryRootPath;
        config.ProxyBaseUrl = request.ProxyBaseUrl;
        config.SyncIntervalHours = request.SyncIntervalHours;
        config.ProxyTimeoutSeconds = request.ProxyTimeoutSeconds;
        Plugin.Instance.SaveConfiguration();

        return ResultFactory.GetResult(Request, new GlobalSettings
        {
            VirtualLibraryRootPath = config.VirtualLibraryRootPath,
            ProxyBaseUrl = config.ProxyBaseUrl,
            SyncIntervalHours = config.SyncIntervalHours,
            ProxyTimeoutSeconds = config.ProxyTimeoutSeconds
        }, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // GET /virtuallib/connectors/{Id}/stats
    // -----------------------------------------------------------------------
    public object Get(GetConnectorStats request)
    {
        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (connectorConfig is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        var virtualLibRoot = config.VirtualLibraryRootPath;
        var stats = new List<LibraryStats>();

        foreach (var lib in connectorConfig.KnownLibraries)
        {
            var dir = Path.Combine(virtualLibRoot, StrmGenerator.SanitizeName(connectorConfig.DisplayName), StrmGenerator.SanitizeName(lib.Name));
            var count = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories).Length
                : 0;

            stats.Add(new LibraryStats
            {
                LibraryId = lib.Id,
                LibraryName = lib.Name,
                EntryCount = count
            });
        }

        return ResultFactory.GetResult(Request, stats, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // POST /virtuallib/connectors/{Id}/libraries/{LibraryId}/sync
    // -----------------------------------------------------------------------
    public object Post(SyncLibrary request)
    {
        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (connectorConfig is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        var virtualLibRoot = config.VirtualLibraryRootPath;

        var lib = connectorConfig.KnownLibraries.FirstOrDefault(l => l.Id == request.LibraryId);
        if (lib != null && !string.IsNullOrEmpty(virtualLibRoot))
            _libraryProvisioner.EnsureVirtualFolder(connectorConfig.DisplayName, lib.Name, lib.Type, virtualLibRoot);

        var result = _syncService.Value.SyncLibraryAsync(
            connectorConfig,
            request.LibraryId,
            virtualLibRoot,
            ProxyBaseUrl,
            progress: null,
            CancellationToken.None).GetAwaiter().GetResult();

        return ResultFactory.GetResult(Request, result, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // POST /virtuallib/connectors/{Id}/test
    // -----------------------------------------------------------------------
    public object Post(TestConnector request)
    {
        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (connectorConfig is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        using var connector = _connectorFactory.Value.Create(connectorConfig);
        var result = connector.TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();

        return ResultFactory.GetResult(Request, result, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // GET /virtuallib/connectors/{Id}/libraries
    // -----------------------------------------------------------------------
    public object Get(GetConnectorLibraries request)
    {
        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (connectorConfig is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        using var connector = _connectorFactory.Value.Create(connectorConfig);
        var libraries = connector.ListLibrariesAsync(CancellationToken.None).GetAwaiter().GetResult();

        var libList = libraries.ToList();

        // Cache known libraries in config
        connectorConfig.KnownLibraries = libList.Select(l => new KnownLibrary
        {
            Id = l.Id,
            Name = l.Name,
            Type = l.Type.ToString()
        }).ToList();
        Plugin.Instance.SaveConfiguration();

        // Provision virtual folders for all discovered libraries
        var virtualLibRoot = config.VirtualLibraryRootPath;
        if (!string.IsNullOrEmpty(virtualLibRoot))
        {
            foreach (var lib in connectorConfig.KnownLibraries)
                _libraryProvisioner.EnsureVirtualFolder(connectorConfig.DisplayName, lib.Name, lib.Type, virtualLibRoot);
        }

        return ResultFactory.GetResult(Request, libList, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // POST /virtuallib/sync
    // -----------------------------------------------------------------------
    public object Post(SyncAll request)
    {
        var config = Plugin.Instance!.Configuration;
        var enabledConnectors = config.Connectors.Where(c => c.Enabled).ToList();
        var virtualLibRoot = config.VirtualLibraryRootPath;

        var results = new List<SyncResult>(enabledConnectors.Count);

        foreach (var connectorConfig in enabledConnectors)
        {
            ProvisionEnabledLibraries(connectorConfig, virtualLibRoot);

            var result = _syncService.Value.SyncConnectorAsync(
                connectorConfig,
                virtualLibRoot,
                ProxyBaseUrl,
                progress: null,
                CancellationToken.None).GetAwaiter().GetResult();

            results.Add(result);
        }

        return ResultFactory.GetResult(Request, results, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // POST /virtuallib/connectors/{Id}/sync
    // -----------------------------------------------------------------------
    public object Post(SyncConnector request)
    {
        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (connectorConfig is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        var virtualLibRoot = config.VirtualLibraryRootPath;
        ProvisionEnabledLibraries(connectorConfig, virtualLibRoot);

        var result = _syncService.Value.SyncConnectorAsync(
            connectorConfig,
            virtualLibRoot,
            ProxyBaseUrl,
            progress: null,
            CancellationToken.None).GetAwaiter().GetResult();

        return ResultFactory.GetResult(Request, result, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private void ProvisionEnabledLibraries(ConnectorConfig connectorConfig, string virtualLibRoot)
    {
        if (string.IsNullOrEmpty(virtualLibRoot)) return;

        var enabledLibs = connectorConfig.KnownLibraries
            .Where(l => connectorConfig.LibraryIds.Contains(l.Id));

        foreach (var lib in enabledLibs)
            _libraryProvisioner.EnsureVirtualFolder(connectorConfig.DisplayName, lib.Name, lib.Type, virtualLibRoot);
    }
}
