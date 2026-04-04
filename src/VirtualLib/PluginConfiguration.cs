using MediaBrowser.Model.Plugins;

namespace VirtualLib;

public enum AuthMode { ApiKey, UserCredentials }

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
}
