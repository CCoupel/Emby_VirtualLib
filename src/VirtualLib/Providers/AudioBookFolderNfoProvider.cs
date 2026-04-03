using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Providers;

/// <summary>
/// Local metadata provider for audiobook <em>folder</em> items.
///
/// In Emby's Audiobooks library, book folders are resolved as <see cref="Folder"/> items
/// (not <c>MusicAlbum</c>) when they contain only .strm files.  This provider reads the
/// <c>album.nfo</c> placed by VirtualLib's sync inside each book folder and applies its
/// title / overview / authors to the <see cref="Folder"/> item so it appears correctly in
/// the Emby UI without requiring a chapter to be played first.
///
/// The provider is safe for non-audiobook folders: if no <c>album.nfo</c> exists inside
/// the folder, <c>HasMetadata = false</c> is returned and Emby keeps its defaults.
/// </summary>
public sealed class AudioBookFolderNfoProvider : ILocalMetadataProvider<Folder>
{
    private readonly ILogger<AudioBookFolderNfoProvider> _logger;

    public string Name => "VirtualLib NFO";

    public AudioBookFolderNfoProvider(ILogger<AudioBookFolderNfoProvider> logger)
    {
        _logger = logger;
    }

    public Task<MetadataResult<Folder>> GetMetadata(
        ItemInfo info,
        LibraryOptions libraryOptions,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Folder> { Item = new Folder() };

        // Folder items: info.Path is the directory itself.
        if (!Directory.Exists(info.Path))
            return Task.FromResult(result);

        var nfoPath = Path.Combine(info.Path, "album.nfo");
        _logger.LogInformation(
            "VirtualLib AudioBookFolderNfoProvider: folder='{Path}' nfo='{NfoPath}' exists={Exists}",
            info.Path, nfoPath, File.Exists(nfoPath));

        var data = NfoXmlReader.Read(nfoPath);
        if (data is null)
            return Task.FromResult(result);

        _logger.LogInformation(
            "VirtualLib: AudioBook folder NFO read OK — title='{Title}'", data.Title);

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

        return Task.FromResult(result);
    }
}
