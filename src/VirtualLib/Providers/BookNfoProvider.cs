using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Providers;

/// <summary>
/// Local metadata provider for Book items.
/// Reads a sidecar NFO file placed next to the book file (same name, .nfo extension),
/// or <c>book.nfo</c> if the item is folder-based.
///
/// NFO format (root element &lt;book&gt;):
/// <code>
/// &lt;book&gt;
///   &lt;title&gt;...&lt;/title&gt;
///   &lt;year&gt;2020&lt;/year&gt;
///   &lt;plot&gt;...&lt;/plot&gt;
///   &lt;rating&gt;8.5&lt;/rating&gt;
///   &lt;author&gt;Author Name&lt;/author&gt;
///   &lt;genre&gt;Fiction&lt;/genre&gt;
///   &lt;studio&gt;Publisher&lt;/studio&gt;
/// &lt;/book&gt;
/// </code>
///
/// Emby has no built-in NFO provider for Book items — this class fills that gap.
/// Auto-discovered by Emby's provider manager from the plugin assembly.
/// </summary>
public sealed class BookNfoProvider : ILocalMetadataProvider<Book>
{
    private readonly ILogger<BookNfoProvider> _logger;

    public string Name => "VirtualLib NFO";

    public BookNfoProvider(ILogger<BookNfoProvider> logger)
    {
        _logger = logger;
    }

    public Task<MetadataResult<Book>> GetMetadata(
        ItemInfo info,
        LibraryOptions libraryOptions,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Book> { Item = new Book() };

        string nfoPath;

        if (Directory.Exists(info.Path))
        {
            // Folder-based book: look for book.nfo inside the folder
            nfoPath = Path.Combine(info.Path, "book.nfo");
        }
        else
        {
            // File-based book (epub, pdf, strm…): sidecar <filename>.nfo
            nfoPath = Path.ChangeExtension(info.Path, ".nfo");
        }

        var data = NfoXmlReader.Read(nfoPath);
        if (data is null)
            return Task.FromResult(result);

        _logger.LogDebug("VirtualLib: reading Book NFO '{Path}'", nfoPath);

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

        // Authors → People list as Writers
        var authors = data.Authors.Count > 0 ? data.Authors
                    : data.AlbumArtists.Count > 0 ? data.AlbumArtists
                    : data.Artists;

        foreach (var author in authors)
        {
            result.AddPerson(new PersonInfo
            {
                Name = author,
                Type = PersonType.Writer
            });
        }

        return Task.FromResult(result);
    }
}
