using MediaBrowser.Model.Plugins;

namespace VirtualLib;

public enum AuthMode { ApiKey, UserCredentials }

public enum LibraryOrganization
{
    /// <summary>Root / ConnectorName / LibraryName / Item  (one Emby library per remote library).</summary>
    Isolated,
    /// <summary>Root / LibraryType / ConnectorName / LibraryName / Item  (one Emby library per content type).</summary>
    SharedByType
}

public enum MetadataMode
{
    /// <summary>Plugin fetches metadata/images from the remote server, skips items that already have a .nfo file.</summary>
    RemoteSync,
    /// <summary>Emby's own fetchers handle metadata/images from online databases (TMDB, TVDB…).</summary>
    LocalScraping,
    /// <summary>Plugin fetches metadata/images from the remote server for all items, overwriting existing .nfo files.</summary>
    RemoteSyncFull
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public List<ConnectorConfig> Connectors { get; set; } = new();
    public string VirtualLibraryRootPath { get; set; } = string.Empty;
    /// <summary>
    /// Base URL used to build .strm proxy links (e.g. https://media.coupel.net/emby).
    /// Leave empty to auto-detect from the incoming request.
    /// </summary>
    public string ProxyBaseUrl { get; set; } = string.Empty;
    public int SyncIntervalHours { get; set; } = 6;
    public int ProxyTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Prefix prepended to the Emby library name when LibraryOrganization = SharedByType.
    /// e.g. "[VL] " → "[VL] Movies". Leave empty for no prefix.
    /// </summary>
    public string SharedLibraryPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Suffix appended to the Emby library name when LibraryOrganization = SharedByType.
    /// e.g. " (VL)" → "Movies (VL)". Leave empty for no suffix.
    /// </summary>
    public string SharedLibrarySuffix { get; set; } = string.Empty;

    // ── Cache ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables local chunk-based caching of proxied media streams.
    /// Each connector can further override this with its own CacheEnabled flag.
    /// </summary>
    public bool CacheEnabled { get; set; } = false;

    /// <summary>
    /// Root directory for the cache. Leave empty to use the Emby cache path
    /// (applicationPaths.CachePath/virtuallib-cache).
    /// </summary>
    public string CacheRootPath { get; set; } = string.Empty;

    /// <summary>Maximum total cache size in gigabytes. Not yet enforced (Phase 2).</summary>
    public long CacheMaxSizeGb { get; set; } = 50;

    /// <summary>
    /// How often data is flushed to disk during streaming (in MB).
    /// Smaller values save more data on early disconnects; larger values mean fewer files before merge.
    /// </summary>
    public int CacheChunkSizeMb { get; set; } = 2;

    /// <summary>Days before an unused cache entry is eligible for eviction (Phase 2).</summary>
    public int CacheTtlDays { get; set; } = 30;
}

public sealed class KnownLibrary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    /// <summary>Cached item count from the remote server (updated on library list refresh).</summary>
    public int RemoteItemCount { get; set; } = -1;
}

public sealed class ConnectorConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = string.Empty;
    public string ServerType { get; set; } = "Emby";
    public string ServerUrl { get; set; } = string.Empty;

    // --- Authentication ---
    public AuthMode AuthMode { get; set; } = AuthMode.ApiKey;
    /// <summary>Used when AuthMode == ApiKey</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Used when AuthMode == UserCredentials</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>Used when AuthMode == UserCredentials. Stored in Emby config (disk-level encryption).</summary>
    public string Password { get; set; } = string.Empty;

    public MetadataMode MetadataMode { get; set; } = MetadataMode.RemoteSync;
    public List<string> LibraryIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public List<KnownLibrary> KnownLibraries { get; set; } = new();

    /// <summary>
    /// For ServerType = "PlexTV" only.
    /// The Plex server's unique machine identifier (clientIdentifier from plex.tv resources API).
    /// </summary>
    public string PlexMachineIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of libraries from this connector synced simultaneously.
    /// Across all connectors everything runs in parallel; this caps concurrency per connector.
    /// </summary>
    public int MaxParallelLibraries { get; set; } = 4;

    /// <summary>
    /// ID of the local Emby user whose personal data (played, favourite, resume position)
    /// is synchronised with this connector.
    /// Playback sessions are forwarded to the remote server for ALL local users,
    /// but only this user's stop position is persisted on the remote side.
    /// Leave empty to disable personal-data sync entirely.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;

    /// <summary>
    /// Controls how the Emby virtual library is named and shared.
    /// Physical files are always at Root/ConnectorName/LibraryName/ in both modes.
    /// Isolated (default): one dedicated Emby library per remote library, named "ConnectorName — LibraryName".
    /// SharedByType: one shared Emby library per content type (Movies, TvShows…); each remote library adds its path.
    /// </summary>
    public LibraryOrganization LibraryOrganization { get; set; } = LibraryOrganization.Isolated;

    /// <summary>
    /// Enables local caching for streams from this connector.
    /// Only effective when the global CacheEnabled is also true.
    /// </summary>
    public bool CacheEnabled { get; set; } = true;
}
