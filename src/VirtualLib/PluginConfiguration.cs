using MediaBrowser.Model.Plugins;

namespace VirtualLib;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public List<ConnectorConfig> Connectors { get; set; } = new();
    public string VirtualLibraryRootPath { get; set; } = string.Empty;
    public int SyncIntervalHours { get; set; } = 6;
    public int ProxyTimeoutSeconds { get; set; } = 30;
}

public sealed class ConnectorConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = string.Empty;
    public string ServerType { get; set; } = "Emby";
    public string ServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public List<string> LibraryIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
}
