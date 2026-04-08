using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core.Cache;

namespace VirtualLib;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Singleton cache manager shared by all proxy requests.
    /// Initialised at plugin load; null only before the plugin is loaded.
    /// </summary>
    public static CacheManager? Cache { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Do NOT access Configuration here — it calls LoadConfiguration() → ConfigurationFilePath
        // → Path.Combine(DataFolderPath, ...) which is null at construction time.
        // Use applicationPaths directly for the default cache root.
        var cacheRoot = System.IO.Path.Combine(applicationPaths.CachePath, "virtuallib-cache");
        Cache = new CacheManager(cacheRoot, NullLogger<CacheManager>.Instance);

        // Fire-and-forget startup cleanup (orphaned .tmp + manifest validation).
        // Errors are swallowed — cache is non-critical.
        _ = Cache.InitializeAsync();
    }

    public override string Name => "VirtualLib";

    public override Guid Id => new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

    public override string Description =>
        "Aggregate remote media server libraries as native virtual libraries on this Emby host.";

    /// <summary>
    /// Returns the embedded PNG icon for this plugin.
    /// </summary>
    public Stream? GetThumbImage()
    {
        var type = GetType();
        return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png");
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
        new[]
        {
            new PluginPageInfo
            {
                Name = "VirtualLibConfig",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web_Pages.config.html",
                EnableInMainMenu = true,
                DisplayName = "VirtualLib",
                MenuSection = "server",
                MenuIcon = "folder_open"
            },
            new PluginPageInfo
            {
                Name = "VirtualLibConfigScript106",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web_Pages.configjs.js"
            }
        };
}
