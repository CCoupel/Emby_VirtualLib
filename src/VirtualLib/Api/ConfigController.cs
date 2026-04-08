using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Connectors;
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
    public string PlexMachineIdentifier { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.ApiKey;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public MetadataMode MetadataMode { get; set; } = MetadataMode.RemoteSync;
    public List<string> LibraryIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int MaxParallelLibraries { get; set; } = 4;
    public LibraryOrganization LibraryOrganization { get; set; } = LibraryOrganization.Isolated;
    public string LocalUserId { get; set; } = string.Empty;
}

[Route("/virtuallib/connectors/{Id}", "PUT", Summary = "Update an existing connector")]
[Authenticated]
public sealed class UpdateConnector : IReturn<ConnectorConfig>
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ServerType { get; set; } = ServerTypes.Emby;
    public string ServerUrl { get; set; } = string.Empty;
    public string PlexMachineIdentifier { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.ApiKey;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public MetadataMode MetadataMode { get; set; } = MetadataMode.RemoteSync;
    public List<string> LibraryIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int MaxParallelLibraries { get; set; } = 4;
    public LibraryOrganization LibraryOrganization { get; set; } = LibraryOrganization.Isolated;
    public string LocalUserId { get; set; } = string.Empty;
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

[Route("/virtuallib/test-connection", "POST", Summary = "Test connection with ad-hoc parameters (before saving)")]
[Authenticated]
public sealed class TestConnectionParams : IReturn<ConnectorTestResult>
{
    public string ServerType { get; set; } = ServerTypes.Emby;
    public string ServerUrl { get; set; } = string.Empty;
    public string PlexMachineIdentifier { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.ApiKey;
    public string ApiKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
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

[Route("/virtuallib/connectors/{Id}/item-counts", "GET", Summary = "Fetch and cache remote item counts per library")]
[Authenticated]
public sealed class GetRemoteItemCounts : IReturn<List<LibraryStats>>
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
    public string SharedLibraryPrefix { get; set; } = string.Empty;
    public string SharedLibrarySuffix { get; set; } = string.Empty;
    public bool CacheEnabled { get; set; } = false;
    public int CacheChunkSizeMb { get; set; } = 2;
    public long CacheMaxSizeGb { get; set; } = 50;
    public int CacheTtlDays { get; set; } = 30;
    public int CacheCompletionThresholdPercent { get; set; } = 90;
}

[Route("/virtuallib/sync", "POST", Summary = "Sync all enabled connectors")]
[Authenticated]
public sealed class SyncAll : IReturn<List<SyncResult>> { }

[Route("/virtuallib/sync/status", "GET", Summary = "Get current sync status")]
[Authenticated]
public sealed class GetSyncStatus : IReturn<SyncStatusResult> { }

[Route("/virtuallib/sync/cancel", "POST", Summary = "Cancel the running sync")]
[Authenticated]
public sealed class CancelSync : IReturnVoid { }

[Route("/virtuallib/plex/servers", "POST", Summary = "List Plex servers visible on plex.tv for the given credentials")]
[Authenticated]
public sealed class GetPlexTvServers : IReturn<PlexTvServersResult>
{
    public AuthMode AuthMode    { get; set; } = AuthMode.ApiKey;
    /// <summary>Plex token (when AuthMode = ApiKey)</summary>
    public string ApiKey        { get; set; } = string.Empty;
    /// <summary>plex.tv username (when AuthMode = UserCredentials)</summary>
    public string Username      { get; set; } = string.Empty;
    /// <summary>plex.tv password (when AuthMode = UserCredentials)</summary>
    public string Password      { get; set; } = string.Empty;
    /// <summary>6-digit TOTP code — required when the account has 2FA enabled</summary>
    public string TwoFactorPin  { get; set; } = string.Empty;
}

public sealed class PlexTvServersResult
{
    /// <summary>Resolved Plex token (long-lived). Use this as ApiKey to avoid re-authenticating.</summary>
    public string ResolvedToken { get; set; } = string.Empty;
    public List<PlexServerInfo> Servers { get; set; } = new();
}

[Route("/virtuallib/connectors/{Id}/sync", "POST", Summary = "Sync a single connector")]
[Authenticated]
public sealed class SyncConnector : IReturn<SyncResult>
{
    public string Id { get; set; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------

public sealed class SyncStatusResult
{
    public bool              IsSyncing   { get; set; }
    public DateTime?         StartedAt   { get; set; }
    public List<SyncResult>? LastResults { get; set; }
    public List<LibrarySyncEntry> Libraries { get; set; } = new();
}

public sealed class SyncStartResult
{
    public bool AlreadyRunning { get; set; }
}

public sealed class GlobalSettings
{
    public string VirtualLibraryRootPath { get; set; } = string.Empty;
    public string ProxyBaseUrl { get; set; } = string.Empty;
    public int SyncIntervalHours { get; set; } = 6;
    public int ProxyTimeoutSeconds { get; set; } = 30;
    public string SharedLibraryPrefix { get; set; } = string.Empty;
    public string SharedLibrarySuffix { get; set; } = string.Empty;
    public bool CacheEnabled { get; set; } = false;
    public int CacheChunkSizeMb { get; set; } = 2;
    public long CacheMaxSizeGb { get; set; } = 50;
    public int CacheTtlDays { get; set; } = 30;
    public int CacheCompletionThresholdPercent { get; set; } = 90;
}

public sealed class LibraryStats
{
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public int RemoteItemCount { get; set; } = -1;
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

    private static readonly Lazy<IConnectorFactory> _connectorFactory =
        new(() => new ConnectorFactory(new DefaultHttpClientFactory(), NullLoggerFactory.Instance));

    private readonly SyncService _syncService;
    private readonly LibraryProvisioner _libraryProvisioner;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ITaskManager _taskManager;

    public ConfigController(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, ITaskManager taskManager, IItemRepository itemRepository, IUserDataManager userDataManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _taskManager = taskManager;
        _libraryProvisioner = new LibraryProvisioner(libraryManager, NullLogger<LibraryProvisioner>.Instance);
        _syncService = new SyncService(
            _connectorFactory.Value,
            new StrmGenerator(),
            new EpubStubGenerator(),
            new NfoGenerator(),
            NullLogger<SyncService>.Instance,
            libraryManager,
            itemRepository,
            userDataManager,
            userManager);
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
            PlexMachineIdentifier = request.PlexMachineIdentifier,
            AuthMode = request.AuthMode,
            ApiKey = request.ApiKey,
            Username = request.Username,
            Password = request.Password,
            MetadataMode = request.MetadataMode,
            LibraryIds = request.LibraryIds,
            Enabled = request.Enabled,
            MaxParallelLibraries = Math.Max(1, request.MaxParallelLibraries),
            LibraryOrganization = request.LibraryOrganization,
            LocalUserId = request.LocalUserId
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
            PlexMachineIdentifier = request.PlexMachineIdentifier,
            AuthMode = request.AuthMode,
            ApiKey = request.ApiKey,
            Username = request.Username,
            // Preserve existing password if the client sent an empty string (placeholder pattern)
            Password = string.IsNullOrEmpty(request.Password) ? existing.Password : request.Password,
            MetadataMode = request.MetadataMode,
            LibraryIds = request.LibraryIds,
            Enabled = request.Enabled,
            KnownLibraries = existing.KnownLibraries,
            MaxParallelLibraries = Math.Max(1, request.MaxParallelLibraries),
            LibraryOrganization = request.LibraryOrganization,
            LocalUserId = request.LocalUserId
        };

        config.Connectors.Add(updated);
        Plugin.Instance.SaveConfiguration();

        // Sync virtual folders: create for newly checked, remove for unchecked
        var virtualLibRoot = config.VirtualLibraryRootPath;
        if (!string.IsNullOrEmpty(virtualLibRoot))
        {
            var addedIds   = request.LibraryIds.Except(existing.LibraryIds).ToList();
            var removedIds = existing.LibraryIds.Except(request.LibraryIds).ToList();

            var globalConfig = Plugin.Instance!.Configuration;
            foreach (var lib in updated.KnownLibraries.Where(l => addedIds.Contains(l.Id)))
                _libraryProvisioner.EnsureVirtualFolder(updated.DisplayName, lib.Name, lib.Type, virtualLibRoot, updated.MetadataMode,
                    updated.LibraryOrganization, globalConfig.SharedLibraryPrefix, globalConfig.SharedLibrarySuffix);

            foreach (var lib in existing.KnownLibraries.Where(l => removedIds.Contains(l.Id)))
            {
                var removeShared = existing.LibraryOrganization == LibraryOrganization.SharedByType
                    && NoRemainingSharedLibraries(config, lib.Type);
                _libraryProvisioner.RemoveVirtualFolder(existing.DisplayName, lib.Name, virtualLibRoot,
                    lib.Type, existing.LibraryOrganization, removeShared,
                    globalConfig.SharedLibraryPrefix, globalConfig.SharedLibrarySuffix);
            }
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

        // Remove all virtual folders for this connector's selected libraries (including files on disk)
        var virtualLibRoot = config.VirtualLibraryRootPath;
        foreach (var lib in existing.KnownLibraries.Where(l => existing.LibraryIds.Contains(l.Id)))
        {
            var removeShared = existing.LibraryOrganization == LibraryOrganization.SharedByType
                && NoRemainingSharedLibraries(config, lib.Type);
            _libraryProvisioner.RemoveVirtualFolder(existing.DisplayName, lib.Name, virtualLibRoot,
                lib.Type, existing.LibraryOrganization, removeShared,
                config.SharedLibraryPrefix, config.SharedLibrarySuffix);
        }
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
            ProxyTimeoutSeconds = config.ProxyTimeoutSeconds,
            SharedLibraryPrefix = config.SharedLibraryPrefix,
            SharedLibrarySuffix = config.SharedLibrarySuffix,
            CacheEnabled = config.CacheEnabled,
            CacheChunkSizeMb = config.CacheChunkSizeMb,
            CacheMaxSizeGb = config.CacheMaxSizeGb,
            CacheTtlDays = config.CacheTtlDays,
            CacheCompletionThresholdPercent = config.CacheCompletionThresholdPercent
        }, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // GET /virtuallib/sync/status
    // -----------------------------------------------------------------------
    public object Get(GetSyncStatus request)
    {
        return ResultFactory.GetResult(Request, new SyncStatusResult
        {
            IsSyncing   = SyncState.IsSyncing,
            StartedAt   = SyncState.StartedAt,
            LastResults = SyncState.LastResults,
            Libraries   = SyncState.Libraries.ToList()
        }, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // POST /virtuallib/sync/cancel
    // -----------------------------------------------------------------------
    public void Post(CancelSync request)
    {
        SyncState.RequestCancel();
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
        config.SharedLibraryPrefix = request.SharedLibraryPrefix;
        config.SharedLibrarySuffix = request.SharedLibrarySuffix;
        config.CacheEnabled = request.CacheEnabled;
        config.CacheChunkSizeMb = request.CacheChunkSizeMb;
        config.CacheMaxSizeGb = request.CacheMaxSizeGb;
        config.CacheTtlDays = request.CacheTtlDays;
        config.CacheCompletionThresholdPercent = request.CacheCompletionThresholdPercent;
        Plugin.Instance.SaveConfiguration();

        // Update the scheduled task trigger to reflect the new interval
        var worker = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask is LibrarySyncJob);
        if (worker != null)
        {
            worker.Triggers = new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(config.SyncIntervalHours).Ticks
                }
            };
        }

        return ResultFactory.GetResult(Request, new GlobalSettings
        {
            VirtualLibraryRootPath = config.VirtualLibraryRootPath,
            ProxyBaseUrl = config.ProxyBaseUrl,
            SyncIntervalHours = config.SyncIntervalHours,
            ProxyTimeoutSeconds = config.ProxyTimeoutSeconds,
            SharedLibraryPrefix = config.SharedLibraryPrefix,
            SharedLibrarySuffix = config.SharedLibrarySuffix,
            CacheEnabled = config.CacheEnabled,
            CacheChunkSizeMb = config.CacheChunkSizeMb,
            CacheMaxSizeGb = config.CacheMaxSizeGb,
            CacheTtlDays = config.CacheTtlDays,
            CacheCompletionThresholdPercent = config.CacheCompletionThresholdPercent
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
                EntryCount = count,
                RemoteItemCount = lib.RemoteItemCount
            });
        }

        return ResultFactory.GetResult(Request, stats, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // GET /virtuallib/connectors/{Id}/item-counts
    // -----------------------------------------------------------------------
    public object Get(GetRemoteItemCounts request)
    {
        var config = Plugin.Instance!.Configuration;
        var connectorConfig = config.Connectors.FirstOrDefault(c => c.Id == request.Id);

        if (connectorConfig is null)
            throw new ResourceNotFoundException($"Connector '{request.Id}' not found.");

        if (connectorConfig.KnownLibraries.Count == 0)
            return ResultFactory.GetResult(Request, new List<LibraryStats>(), NoHeaders);

        try
        {
            using var connector = _connectorFactory.Value.Create(connectorConfig);
            Task.WhenAll(connectorConfig.KnownLibraries
                .Select(lib => connector.GetItemCountAsync(lib.Id, CancellationToken.None)
                    .ContinueWith(t => { if (t.IsCompletedSuccessfully) lib.RemoteItemCount = t.Result; },
                        TaskContinuationOptions.ExecuteSynchronously))
            ).GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }

        Plugin.Instance.SaveConfiguration();

        var stats = connectorConfig.KnownLibraries.Select(lib => new LibraryStats
        {
            LibraryId = lib.Id,
            LibraryName = lib.Name,
            EntryCount = 0,   // not computed here — use /stats for local count
            RemoteItemCount = lib.RemoteItemCount
        }).ToList();

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

        if (!SyncState.TryStart())
            return ResultFactory.GetResult(Request, new SyncStartResult { AlreadyRunning = true }, NoHeaders);

        var known     = connectorConfig.KnownLibraries.FirstOrDefault(l => l.Id == request.LibraryId);
        var libName   = known?.Name ?? request.LibraryId;
        var mediaType = known?.Type ?? string.Empty;
        SyncState.RegisterLibrary(connectorConfig.Id, connectorConfig.DisplayName, request.LibraryId, libName, mediaType);

        var syncSvc   = _syncService;
        var libMon    = _libraryMonitor;
        var root      = config.VirtualLibraryRootPath;
        var proxyUrl  = ProxyBaseUrl;
        var conn      = connectorConfig;
        var libraryId = request.LibraryId;

        _ = Task.Run(async () =>
        {
            var results = new System.Collections.Concurrent.ConcurrentBag<SyncResult>();
            try
            {
                await SyncLibraryAutonomousAsync(conn, libraryId, root, proxyUrl, syncSvc, libMon,
                    new SemaphoreSlim(1, 1), results, SyncState.CancellationToken);
            }
            catch (OperationCanceledException) { /* tasks already marked failed */ }
            finally
            {
                SyncState.Finish(results.ToList());
            }
        });

        return ResultFactory.GetResult(Request, new SyncStartResult { AlreadyRunning = false }, NoHeaders);
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
    // POST /virtuallib/test-connection  (ad-hoc, before saving)
    // -----------------------------------------------------------------------
    public object Post(TestConnectionParams request)
    {
        var tempConfig = new ConnectorConfig
        {
            Id = "test",
            DisplayName = "test",
            ServerType = request.ServerType,
            ServerUrl = request.ServerUrl,
            PlexMachineIdentifier = request.PlexMachineIdentifier,
            AuthMode = request.AuthMode,
            ApiKey = request.ApiKey,
            Username = request.Username,
            Password = request.Password
        };

        using var connector = _connectorFactory.Value.Create(tempConfig);
        var result = connector.TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
        return ResultFactory.GetResult(Request, result, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // POST /virtuallib/plex/servers
    // -----------------------------------------------------------------------
    public object Post(GetPlexTvServers request)
    {
        var ct      = CancellationToken.None;
        var factory = new DefaultHttpClientFactory();

        string token;
        if (request.AuthMode == AuthMode.ApiKey)
        {
            if (string.IsNullOrEmpty(request.ApiKey))
                throw new ArgumentException("ApiKey (Plex token) is required.");
            token = request.ApiKey;
        }
        else
        {
            if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
                throw new ArgumentException("Username and Password are required for UserCredentials mode.");
            token = PlexTvConnector
                .GetTokenFromCredentialsAsync(request.Username, request.Password, factory, ct,
                    twoFactorPin: request.TwoFactorPin)
                .GetAwaiter().GetResult();
        }

        var servers = PlexTvConnector.ListServersAsync(token, factory, ct).GetAwaiter().GetResult();

        return ResultFactory.GetResult(Request,
            new PlexTvServersResult { ResolvedToken = token, Servers = servers },
            NoHeaders);
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

        // Cache known libraries in config, preserving existing RemoteItemCount
        var existingCounts = connectorConfig.KnownLibraries
            .ToDictionary(l => l.Id, l => l.RemoteItemCount);

        connectorConfig.KnownLibraries = libList.Select(l => new KnownLibrary
        {
            Id = l.Id,
            Name = l.Name,
            Type = l.Type.ToString(),
            RemoteItemCount = existingCounts.GetValueOrDefault(l.Id, -1)
        }).ToList();

        // Fetch remote item counts in parallel — reuse the same connector (already authenticated)
        try
        {
            Task.WhenAll(connectorConfig.KnownLibraries
                .Select(lib => connector.GetItemCountAsync(lib.Id, CancellationToken.None)
                    .ContinueWith(t => { if (t.IsCompletedSuccessfully) lib.RemoteItemCount = t.Result; },
                        TaskContinuationOptions.ExecuteSynchronously))
            ).GetAwaiter().GetResult();
        }
        catch { /* best-effort — don't fail the list if counts can't be fetched */ }

        Plugin.Instance.SaveConfiguration();

        // Provision virtual folders only for user-selected (enabled) libraries
        var virtualLibRoot = config.VirtualLibraryRootPath;
        if (!string.IsNullOrEmpty(virtualLibRoot))
        {
            var gc = Plugin.Instance!.Configuration;
            foreach (var lib in connectorConfig.KnownLibraries.Where(l => connectorConfig.LibraryIds.Contains(l.Id)))
                _libraryProvisioner.EnsureVirtualFolder(
                    connectorConfig.DisplayName, lib.Name, lib.Type, virtualLibRoot, connectorConfig.MetadataMode,
                    connectorConfig.LibraryOrganization, gc.SharedLibraryPrefix, gc.SharedLibrarySuffix);
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

        if (!SyncState.TryStart())
            return ResultFactory.GetResult(Request, new SyncStartResult { AlreadyRunning = true }, NoHeaders);

        // Pre-register all libraries for immediate UI display
        foreach (var conn in enabledConnectors)
        foreach (var libId in conn.LibraryIds)
        {
            var known = conn.KnownLibraries.FirstOrDefault(l => l.Id == libId);
            SyncState.RegisterLibrary(conn.Id, conn.DisplayName, libId,
                known?.Name ?? libId, known?.Type ?? string.Empty);
        }

        var syncSvc  = _syncService;
        var libMon   = _libraryMonitor;
        var root     = config.VirtualLibraryRootPath;
        var proxyUrl = ProxyBaseUrl;
        var conns    = enabledConnectors;

        _ = Task.Run(async () =>
        {
            var results   = new System.Collections.Concurrent.ConcurrentBag<SyncResult>();
            var semaphores = conns.ToDictionary(
                c => c.Id,
                c => new SemaphoreSlim(Math.Max(1, c.MaxParallelLibraries)));

            var allTasks = conns
                .SelectMany(conn => conn.LibraryIds.Select(libId =>
                    SyncLibraryAutonomousAsync(conn, libId, root, proxyUrl, syncSvc, libMon,
                        semaphores[conn.Id], results, SyncState.CancellationToken)))
                .ToList();

            try
            {
                await Task.WhenAll(allTasks);
                Plugin.Instance?.SaveConfiguration();
            }
            catch (OperationCanceledException) { /* tasks already marked failed */ }
            finally
            {
                SyncState.Finish(results.ToList());
            }
        });

        return ResultFactory.GetResult(Request, new SyncStartResult { AlreadyRunning = false }, NoHeaders);
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

        if (!SyncState.TryStart())
            return ResultFactory.GetResult(Request, new SyncStartResult { AlreadyRunning = true }, NoHeaders);

        foreach (var libId in connectorConfig.LibraryIds)
        {
            var known = connectorConfig.KnownLibraries.FirstOrDefault(l => l.Id == libId);
            SyncState.RegisterLibrary(connectorConfig.Id, connectorConfig.DisplayName, libId,
                known?.Name ?? libId, known?.Type ?? string.Empty);
        }

        var syncSvc  = _syncService;
        var libMon   = _libraryMonitor;
        var root     = config.VirtualLibraryRootPath;
        var proxyUrl = ProxyBaseUrl;
        var conn     = connectorConfig;

        _ = Task.Run(async () =>
        {
            var results   = new System.Collections.Concurrent.ConcurrentBag<SyncResult>();
            var semaphore = new SemaphoreSlim(Math.Max(1, conn.MaxParallelLibraries));

            var tasks = conn.LibraryIds
                .Select(libId => SyncLibraryAutonomousAsync(conn, libId, root, proxyUrl, syncSvc, libMon,
                    semaphore, results, SyncState.CancellationToken))
                .ToList();

            try
            {
                await Task.WhenAll(tasks);
                Plugin.Instance?.SaveConfiguration();
            }
            catch (OperationCanceledException) { /* tasks already marked failed */ }
            finally
            {
                SyncState.Finish(results.ToList());
            }
        });

        return ResultFactory.GetResult(Request, new SyncStartResult { AlreadyRunning = false }, NoHeaders);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs Phase 1 then Phase 2 for a single library, fully autonomously.
    /// Acquires <paramref name="semaphore"/> for the duration of Phase 1 only
    /// (Phase 2 is I/O-bound on Emby's side, not the remote server).
    /// Updates <see cref="SyncState"/> throughout.
    /// </summary>
    private static async Task SyncLibraryAutonomousAsync(
        ConnectorConfig conn,
        string libraryId,
        string root,
        string proxyUrl,
        SyncService syncSvc,
        ILibraryMonitor libMon,
        SemaphoreSlim semaphore,
        System.Collections.Concurrent.ConcurrentBag<SyncResult> results,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            // ── Phase 1: generate .strm / .nfo files ────────────────────────
            var prog1 = new Progress<SyncProgress>(p =>
                SyncState.UpdatePhase1(conn.Id, libraryId, p.Current, p.Total));

            var (result, pending) = await syncSvc.SyncLibraryAsync(
                conn, libraryId, root, proxyUrl, prog1, ct);

            results.Add(result);

            var lp = pending.FirstOrDefault();
            if (lp is not null && Directory.Exists(lp.LibraryFolderPath))
                libMon.ReportFileSystemChanged(lp.LibraryFolderPath);

            // Ensure Phase1 counters reflect 100 % even if progress wasn't reported
            if (result.Success)
            {
                var total = result.ItemsCreated + result.ItemsSkipped + result.ItemsFailed;
                SyncState.UpdatePhase1(conn.Id, libraryId, total, total);
            }

            semaphore.Release();    // free slot for next Phase-1 task

            // ── Phase 2: push metadata (runs outside the semaphore) ─────────
            if (lp is not null && lp.Items.Count > 0)
            {
                SyncState.Phase2Start(conn.Id, libraryId, lp.Items.Count);

                // Give Emby's per-library scanner time to index the new files
                await Task.Delay(5_000, ct);

                var prog2 = new Progress<SyncProgress>(p =>
                    SyncState.UpdatePhase2(conn.Id, libraryId, p.Current, p.Total));

                await syncSvc.PushMetadataAsync(lp.Items, lp.LibraryName, prog2, ct, conn.LocalUserId);
            }

            SyncState.MarkDone(conn.Id, libraryId);
        }
        catch (OperationCanceledException)
        {
            SyncState.MarkFailed(conn.Id, libraryId, "Cancelled");
            try { semaphore.Release(); } catch { /* already released */ }
            throw;
        }
        catch (Exception ex)
        {
            SyncState.MarkFailed(conn.Id, libraryId, ex.Message);
            results.Add(SyncResult.Failure(conn.DisplayName, ex.Message, TimeSpan.Zero));
            try { semaphore.Release(); } catch { /* already released */ }
        }
    }

    /// <summary>
    /// Returns true if no connector in the current configuration (after updates/deletions)
    /// has a SharedByType-selected library whose normalized type matches <paramref name="libraryType"/>.
    /// Used to decide whether to remove the shared Emby virtual folder when the last library of a type is removed.
    /// </summary>
    private static bool NoRemainingSharedLibraries(PluginConfiguration config, string libraryType)
    {
        var normalized = LibraryProvisioner.NormalizeLibraryType(libraryType);
        return !config.Connectors
            .Where(c => c.LibraryOrganization == LibraryOrganization.SharedByType)
            .SelectMany(c => c.KnownLibraries.Where(kl => c.LibraryIds.Contains(kl.Id)))
            .Any(kl => LibraryProvisioner.NormalizeLibraryType(kl.Type) == normalized);
    }

    private void ProvisionEnabledLibraries(ConnectorConfig connectorConfig, string virtualLibRoot)
    {
        if (string.IsNullOrEmpty(virtualLibRoot)) return;

        var enabledLibs = connectorConfig.KnownLibraries
            .Where(l => connectorConfig.LibraryIds.Contains(l.Id));

        var gc = Plugin.Instance?.Configuration;
        foreach (var lib in enabledLibs)
            _libraryProvisioner.EnsureVirtualFolder(
                connectorConfig.DisplayName, lib.Name, lib.Type, virtualLibRoot, connectorConfig.MetadataMode,
                connectorConfig.LibraryOrganization, gc?.SharedLibraryPrefix ?? "", gc?.SharedLibrarySuffix ?? "");
    }
}
