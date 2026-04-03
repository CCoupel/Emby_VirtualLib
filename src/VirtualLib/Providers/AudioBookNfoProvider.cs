using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Providers;

/// <summary>
/// Local metadata provider for AudioBook items (type <see cref="Audio"/> in an Audiobooks library).
/// Reads an <c>album.nfo</c> file placed in the same folder as the .strm file.
///
/// NFO format (root element &lt;album&gt;):
/// <code>
/// &lt;album&gt;
///   &lt;title&gt;...&lt;/title&gt;
///   &lt;year&gt;2020&lt;/year&gt;
///   &lt;review&gt;...&lt;/review&gt;
///   &lt;rating&gt;8.5&lt;/rating&gt;
///   &lt;albumartist&gt;Author Name&lt;/albumartist&gt;
///   &lt;artist&gt;Author Name&lt;/artist&gt;
///   &lt;genre&gt;Fiction&lt;/genre&gt;
/// &lt;/album&gt;
/// </code>
///
/// Emby has no built-in NFO provider for audiobook items — this class fills that gap.
/// Auto-discovered by Emby's provider manager from the plugin assembly.
/// </summary>
public sealed class AudioBookNfoProvider : ILocalMetadataProvider<Audio>
{
    private readonly ILogger<AudioBookNfoProvider> _logger;

    public string Name => "VirtualLib NFO";

    public AudioBookNfoProvider(ILogger<AudioBookNfoProvider> logger)
    {
        _logger = logger;
    }

    public Task<MetadataResult<Audio>> GetMetadata(
        ItemInfo info,
        LibraryOptions libraryOptions,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Audio> { Item = new Audio() };

        // Determine where to look for album.nfo:
        // - If the path is a directory (folder-based Audio item), the NFO is inside it.
        // - If the path is a file (.strm chapter), the NFO is in the parent directory.
        string nfoPath;
        if (Directory.Exists(info.Path))
        {
            nfoPath = Path.Combine(info.Path, "album.nfo");
        }
        else
        {
            var folder = Path.GetDirectoryName(info.Path);
            if (string.IsNullOrEmpty(folder))
                return Task.FromResult(result);
            nfoPath = Path.Combine(folder, "album.nfo");
        }

        _logger.LogInformation("VirtualLib AudioBookNfoProvider invoked: path='{Path}' nfo='{NfoPath}' exists={Exists}",
            info.Path, nfoPath, File.Exists(nfoPath));
        var data = NfoXmlReader.Read(nfoPath);
        if (data is null)
            return Task.FromResult(result);

        _logger.LogInformation("VirtualLib: AudioBook NFO read OK — title='{Title}'", data.Title);

        // For an Audio (chapter) item we only set grouping fields.
        // Name stays as-is (filename-derived chapter title).
        // Book-level fields (Overview, Genres, Studios…) belong on the Folder container, not here.
        result.HasMetadata = true;

        // Album = book title → Emby uses this to group chapters into the audiobook container
        if (!string.IsNullOrEmpty(data.Title))
            result.Item.Album = data.Title;

        if (data.ProductionYear.HasValue)
            result.Item.ProductionYear = data.ProductionYear.Value;

        // Authors → AlbumArtists (primary) + Artists (compat)
        var authors = data.AlbumArtists.Count > 0 ? data.AlbumArtists : data.Artists;
        if (authors.Count > 0)
        {
            result.Item.AlbumArtists = authors.ToArray();
            result.Item.Artists = authors.ToArray();
        }

        return Task.FromResult(result);
    }
}
