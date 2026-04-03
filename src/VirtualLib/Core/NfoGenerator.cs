using System.Text;
using System.Xml;
using VirtualLib.Core.Models;

namespace VirtualLib.Core;

public sealed class NfoGenerator
{
    // UTF-8 without BOM — XmlWriter backed by a MemoryStream correctly sets encoding="utf-8"
    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Indent = true,
        IndentChars = "  ",
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        OmitXmlDeclaration = false
    };

    /// <summary>
    /// Génère le fichier .nfo pour un item.
    /// Retourne le chemin du fichier créé.
    /// </summary>
    public string Generate(MediaMetadata metadata, string nfoDirectory)
    {
        Directory.CreateDirectory(nfoDirectory);

        string content;
        string fileName;

        if (metadata.Type == MediaType.Movie)
        {
            content = GenerateMovieNfo(metadata);
            fileName = StrmGenerator.SanitizeName(metadata.Year.HasValue
                ? $"{metadata.Title} ({metadata.Year})"
                : metadata.Title) + ".nfo";
        }
        else if (metadata.Type == MediaType.Episode)
        {
            content = GenerateEpisodeNfo(metadata);
            var s = metadata.SeasonNumber ?? 0;
            var e = metadata.EpisodeNumber ?? 0;
            var seriesName = StrmGenerator.SanitizeName(metadata.SeriesName ?? metadata.Title);
            fileName = $"{seriesName} - S{s:D2}E{e:D2}.nfo";
        }
        else if (metadata.Type == MediaType.AudioBook)
        {
            // Audiobook library: album-level NFO read by Emby's Music/AudioBook scanner
            content = GenerateMusicAlbumNfo(metadata);
            fileName = "album.nfo";
        }
        else if (metadata.Type == MediaType.Book)
        {
            content = GenerateBookNfo(metadata);
            fileName = StrmGenerator.SanitizeName(metadata.Year.HasValue
                ? $"{metadata.Title} ({metadata.Year})"
                : metadata.Title) + ".nfo";
        }
        else
        {
            // Photos and Music: no NFO needed
            return string.Empty;
        }

        var filePath = Path.Combine(nfoDirectory, fileName);
        File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return filePath;
    }

    public string GenerateMovieNfo(MediaMetadata metadata)
    {
        using var ms = new MemoryStream();
        using var writer = XmlWriter.Create(ms, XmlSettings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("movie");

        writer.WriteElementString("title", metadata.Title);
        if (metadata.Year.HasValue)
            writer.WriteElementString("year", metadata.Year.Value.ToString());
        if (!string.IsNullOrEmpty(metadata.Tagline))
            writer.WriteElementString("tagline", metadata.Tagline);
        if (!string.IsNullOrEmpty(metadata.Overview))
            writer.WriteElementString("plot", metadata.Overview);
        if (metadata.CommunityRating.HasValue)
            writer.WriteElementString("rating", metadata.CommunityRating.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        if (metadata.RuntimeMinutes.HasValue)
            writer.WriteElementString("runtime", metadata.RuntimeMinutes.Value.ToString());
        if (!string.IsNullOrEmpty(metadata.OfficialRating))
            writer.WriteElementString("mpaa", metadata.OfficialRating);
        if (!string.IsNullOrEmpty(metadata.TrailerUrl))
            writer.WriteElementString("trailer", metadata.TrailerUrl);

        foreach (var genre in metadata.Genres)
            writer.WriteElementString("genre", genre);

        foreach (var studio in metadata.Studios)
            writer.WriteElementString("studio", studio);

        foreach (var tag in metadata.Tags)
            writer.WriteElementString("tag", tag);

        foreach (var director in metadata.Directors)
            writer.WriteElementString("director", director);

        foreach (var writer_ in metadata.Writers)
            writer.WriteElementString("credits", writer_);

        if (!string.IsNullOrEmpty(metadata.ImdbId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "imdb");
            writer.WriteAttributeString("default", "true");
            writer.WriteString(metadata.ImdbId);
            writer.WriteEndElement();
        }

        if (!string.IsNullOrEmpty(metadata.TmdbId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tmdb");
            writer.WriteString(metadata.TmdbId);
            writer.WriteEndElement();
        }

        foreach (var person in metadata.Cast)
        {
            writer.WriteStartElement("actor");
            writer.WriteElementString("name", person.Name);
            if (!string.IsNullOrEmpty(person.Role))
                writer.WriteElementString("role", person.Role);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // movie
        writer.WriteEndDocument();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string GenerateEpisodeNfo(MediaMetadata metadata)
    {
        using var ms = new MemoryStream();
        using var writer = XmlWriter.Create(ms, XmlSettings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("episodedetails");

        writer.WriteElementString("title", metadata.Title);
        if (!string.IsNullOrEmpty(metadata.SeriesName))
            writer.WriteElementString("showtitle", metadata.SeriesName);
        if (metadata.SeasonNumber.HasValue)
            writer.WriteElementString("season", metadata.SeasonNumber.Value.ToString());
        if (metadata.EpisodeNumber.HasValue)
            writer.WriteElementString("episode", metadata.EpisodeNumber.Value.ToString());
        if (!string.IsNullOrEmpty(metadata.Overview))
            writer.WriteElementString("plot", metadata.Overview);
        if (metadata.CommunityRating.HasValue)
            writer.WriteElementString("rating", metadata.CommunityRating.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        if (metadata.RuntimeMinutes.HasValue)
            writer.WriteElementString("runtime", metadata.RuntimeMinutes.Value.ToString());

        foreach (var director in metadata.Directors)
            writer.WriteElementString("director", director);

        foreach (var writer_ in metadata.Writers)
            writer.WriteElementString("credits", writer_);

        if (!string.IsNullOrEmpty(metadata.TvdbId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tvdb");
            writer.WriteAttributeString("default", "true");
            writer.WriteString(metadata.TvdbId);
            writer.WriteEndElement();
        }

        foreach (var person in metadata.Cast)
        {
            writer.WriteStartElement("actor");
            writer.WriteElementString("name", person.Name);
            if (!string.IsNullOrEmpty(person.Role))
                writer.WriteElementString("role", person.Role);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // episodedetails
        writer.WriteEndDocument();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Generates an album.nfo for an audiobook.
    /// Root element is &lt;album&gt; as written by Emby's AlbumNfoSaver.
    /// Authors map to &lt;albumartist&gt; (primary) and &lt;artist&gt; (compat).
    /// The library must be of type "books" (not "audiobooks") for Emby's
    /// AudioResolver to group .strm files into AudioBook containers.
    /// </summary>
    public string GenerateMusicAlbumNfo(MediaMetadata metadata)
    {
        using var ms = new MemoryStream();
        using var writer = XmlWriter.Create(ms, XmlSettings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("album");

        writer.WriteElementString("title", metadata.Title);
        if (metadata.Year.HasValue)
            writer.WriteElementString("year", metadata.Year.Value.ToString());
        if (!string.IsNullOrEmpty(metadata.Overview))
            writer.WriteElementString("review", metadata.Overview);
        if (metadata.CommunityRating.HasValue)
            writer.WriteElementString("rating", metadata.CommunityRating.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));

        foreach (var author in metadata.Authors)
        {
            writer.WriteElementString("albumartist", author);
            writer.WriteElementString("artist", author);
        }

        foreach (var genre in metadata.Genres)
            writer.WriteElementString("genre", genre);

        foreach (var tag in metadata.Tags)
            writer.WriteElementString("tag", tag);

        writer.WriteEndElement(); // album
        writer.WriteEndDocument();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string GenerateBookNfo(MediaMetadata metadata)
    {
        using var ms = new MemoryStream();
        using var writer = XmlWriter.Create(ms, XmlSettings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("book");

        writer.WriteElementString("title", metadata.Title);
        if (metadata.Year.HasValue)
            writer.WriteElementString("year", metadata.Year.Value.ToString());
        if (!string.IsNullOrEmpty(metadata.Overview))
            writer.WriteElementString("plot", metadata.Overview);
        if (metadata.CommunityRating.HasValue)
            writer.WriteElementString("rating", metadata.CommunityRating.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(metadata.OfficialRating))
            writer.WriteElementString("mpaa", metadata.OfficialRating);

        foreach (var author in metadata.Authors)
            writer.WriteElementString("author", author);

        foreach (var genre in metadata.Genres)
            writer.WriteElementString("genre", genre);

        foreach (var studio in metadata.Studios)
            writer.WriteElementString("studio", studio);

        foreach (var tag in metadata.Tags)
            writer.WriteElementString("tag", tag);

        if (!string.IsNullOrEmpty(metadata.ImdbId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "imdb");
            writer.WriteString(metadata.ImdbId);
            writer.WriteEndElement();
        }

        if (!string.IsNullOrEmpty(metadata.TmdbId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tmdb");
            writer.WriteString(metadata.TmdbId);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // book
        writer.WriteEndDocument();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
