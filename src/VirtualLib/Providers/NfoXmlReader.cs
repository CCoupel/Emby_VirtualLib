using System.Xml;

namespace VirtualLib.Providers;

/// <summary>
/// Shared NFO XML parsing logic for AudioBook and Book providers.
/// </summary>
internal static class NfoXmlReader
{
    internal sealed class NfoData
    {
        public string? Title { get; set; }
        public string? Overview { get; set; }
        public float? CommunityRating { get; set; }
        public int? ProductionYear { get; set; }
        public string? OfficialRating { get; set; }
        public List<string> Genres { get; } = new();
        public List<string> Tags { get; } = new();
        public List<string> Studios { get; } = new();
        public List<string> AlbumArtists { get; } = new();
        public List<string> Artists { get; } = new();
        // For Book: authors stored as writers in the People list
        public List<string> Authors { get; } = new();
    }

    /// <summary>
    /// Reads a NFO XML file and returns its contents.
    /// Supports root elements: &lt;album&gt;, &lt;book&gt;, &lt;audiobook&gt;.
    /// </summary>
    internal static NfoData? Read(string nfoPath)
    {
        if (!File.Exists(nfoPath))
            return null;

        var data = new NfoData();

        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore
        };

        using var reader = XmlReader.Create(nfoPath, settings);

        // Move past the XML declaration and root element
        if (!reader.ReadToFollowing(reader.MoveToContent().ToString()))
            reader.Read();

        // We're now inside the root element — iterate children
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.LocalName.ToLowerInvariant())
            {
                case "title":
                case "localtitle":
                    data.Title = reader.ReadElementContentAsString();
                    break;
                case "plot":
                case "review":
                case "biography":
                    if (string.IsNullOrEmpty(data.Overview))
                        data.Overview = reader.ReadElementContentAsString();
                    else
                        reader.Skip();
                    break;
                case "year":
                    if (int.TryParse(reader.ReadElementContentAsString(), out var year))
                        data.ProductionYear = year;
                    break;
                case "rating":
                    if (float.TryParse(reader.ReadElementContentAsString(),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var rating))
                        data.CommunityRating = rating;
                    break;
                case "mpaa":
                    data.OfficialRating = reader.ReadElementContentAsString();
                    break;
                case "genre":
                    var genre = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(genre)) data.Genres.Add(genre);
                    break;
                case "tag":
                case "style":
                    var tag = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(tag)) data.Tags.Add(tag);
                    break;
                case "studio":
                    var studio = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(studio)) data.Studios.Add(studio);
                    break;
                case "albumartist":
                    var albumArtist = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(albumArtist)) data.AlbumArtists.Add(albumArtist);
                    break;
                case "artist":
                    var artist = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(artist)) data.Artists.Add(artist);
                    break;
                case "author":
                    var author = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(author)) data.Authors.Add(author);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(data.Title) ? null : data;
    }
}
