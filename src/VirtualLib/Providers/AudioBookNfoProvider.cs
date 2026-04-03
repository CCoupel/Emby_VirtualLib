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

        // The NFO sits in the same folder as the .strm file.
        // Audio items are always file-based, so take the parent directory.
        var folder = Path.GetDirectoryName(info.Path);
        if (string.IsNullOrEmpty(folder))
            return Task.FromResult(result);

        var nfoPath = Path.Combine(folder, "album.nfo");
        var data = NfoXmlReader.Read(nfoPath);
        if (data is null)
            return Task.FromResult(result);

        _logger.LogDebug("VirtualLib: reading AudioBook NFO '{Path}'", nfoPath);

        result.HasMetadata = true;
        result.Item.Name = data.Title;
        result.Item.Overview = data.Overview;
        result.Item.CommunityRating = data.CommunityRating;
        result.Item.OfficialRating = data.OfficialRating;

        if (data.ProductionYear.HasValue)
            result.Item.ProductionYear = data.ProductionYear.Value;

        if (data.Genres.Count > 0)
            result.Item.Genres = data.Genres.ToArray();

        if (data.Tags.Count > 0)
            result.Item.Tags = data.Tags.ToArray();

        if (data.Studios.Count > 0)
            result.Item.Studios = data.Studios.ToArray();

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
