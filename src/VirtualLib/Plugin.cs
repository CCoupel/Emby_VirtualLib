using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core.Cache;

namespace VirtualLib;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
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

    public override Guid Id => new Guid("f760af55-1af9-424d-9e85-d405a91e173d");

    public override string Description =>
        "Aggregate remote media server libraries as native virtual libraries on this Emby host.";

    public ImageFormat ThumbImageFormat => ImageFormat.Png;

    /// <summary>
    /// Returns the embedded PNG icon for this plugin (IHasThumbImage).
    /// </summary>
    public Stream GetThumbImage()
    {
        var type = GetType();
        return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png")!;
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
        new[]
        {
            new PluginPageInfo
            {
                // Versioned like VirtualLibConfigScript below (bump on every config.html change).
                // Never bumped since introduction — unlike the script name, which has been bumped
                // 5 times (105→109) specifically to defeat browser caching of stale content served
                // under the same resource name. config.html itself was never protected the same
                // way: a browser holding a cached copy keeps whatever <script> version *that* stale
                // HTML points to via data-controller, so it never even requests the current script —
                // no reload of configjs.js alone can fix it. This is the same root cause already
                // diagnosed once for the exact "cache checkbox unchecked until full reload" symptom
                // (61af5b5) — that fix only bumped the script, leaving this page unprotected against
                // recurrence (bug report post-#44/QUALIF, same symptom, script already at 109).
                //
                // This number is an INDEPENDENT counter from VirtualLibConfigScript's below — they
                // only happen to read "109" on both right now because this page's counter started
                // fresh at the script's current value. Bump whichever file actually changed; do not
                // assume the two must move together next time (see #46 — no automated mechanism yet
                // for either, so nothing here enforces they stay in sync).
                Name = "VirtualLibConfig109",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web_Pages.config.html",
                EnableInMainMenu = true,
                DisplayName = "VirtualLib",
                MenuSection = "server",
                MenuIcon = "folder_open",
                // Never set before this fix. Without it, Emby's Plugins-list detail view has no
                // declared settings page for this plugin and likely falls back to some other
                // (possibly catalog/package-lookup) mechanism when clicked from Dashboard >
                // Plugins — a plausible explanation for the "request execution" error popup
                // reported from that specific entry point, distinct from and unrelated to the
                // main-menu access path (EnableInMainMenu above), which already reaches this same
                // page via Emby's SPA router. Urgent regression investigation (#44).
                IsMainConfigPage = true
            },
            new PluginPageInfo
            {
                Name = "VirtualLibConfigScript109",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web_Pages.configjs.js"
            }
        };
}
