using System.Text;
using System.Xml;
using VirtualLib.Core.Models;

namespace VirtualLib.Core;

public sealed class NfoGenerator
{
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
        else
        {
            return string.Empty;
        }

        var filePath = Path.Combine(nfoDirectory, fileName);
        File.WriteAllText(filePath, content, Encoding.UTF8);
        return filePath;
    }

    public string GenerateMovieNfo(MediaMetadata metadata)
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        });

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

        return sb.ToString();
    }

    public string GenerateEpisodeNfo(MediaMetadata metadata)
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        });

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

        return sb.ToString();
    }
}
