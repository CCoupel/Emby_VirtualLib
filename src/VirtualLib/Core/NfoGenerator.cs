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
            fileName = "movie.nfo";
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
            content = GenerateAudioBookNfo(metadata);
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

        WriteStreamDetails(writer, metadata.Technical, metadata.RuntimeTicks);

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

        WriteStreamDetails(writer, metadata.Technical, metadata.RuntimeTicks);

        writer.WriteEndElement(); // episodedetails
        writer.WriteEndDocument();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string GenerateShowNfo(MediaMetadata metadata)
    {
        using var ms = new MemoryStream();
        using var writer = XmlWriter.Create(ms, XmlSettings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("tvshow");

        writer.WriteElementString("title", metadata.Title);
        if (metadata.Year.HasValue)
            writer.WriteElementString("year", metadata.Year.Value.ToString());
        if (!string.IsNullOrEmpty(metadata.Overview))
            writer.WriteElementString("plot", metadata.Overview);
        if (metadata.CommunityRating.HasValue)
            writer.WriteElementString("rating", metadata.CommunityRating.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(metadata.OfficialRating))
            writer.WriteElementString("mpaa", metadata.OfficialRating);

        foreach (var genre in metadata.Genres)
            writer.WriteElementString("genre", genre);

        foreach (var studio in metadata.Studios)
            writer.WriteElementString("studio", studio);

        foreach (var tag in metadata.Tags)
            writer.WriteElementString("tag", tag);

        if (!string.IsNullOrEmpty(metadata.TvdbId))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", "tvdb");
            writer.WriteAttributeString("default", "true");
            writer.WriteString(metadata.TvdbId);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // tvshow
        writer.WriteEndDocument();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public string GenerateSeasonNfo(MediaMetadata metadata)
    {
        using var ms = new MemoryStream();
        using var writer = XmlWriter.Create(ms, XmlSettings);

        writer.WriteStartDocument(true);
        writer.WriteStartElement("season");

        writer.WriteElementString("title", metadata.Title);
        if (metadata.SeasonNumber.HasValue)
            writer.WriteElementString("seasonnumber", metadata.SeasonNumber.Value.ToString());
        if (!string.IsNullOrEmpty(metadata.Overview))
            writer.WriteElementString("plot", metadata.Overview);
        if (metadata.Year.HasValue)
            writer.WriteElementString("year", metadata.Year.Value.ToString());

        writer.WriteEndElement(); // season
        writer.WriteEndDocument();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Patches an existing NFO file by injecting or replacing the &lt;fileinfo&gt; block.
    /// Safe to call if the file does not exist or if tech/runtimeTicks are null.
    /// </summary>
    public void PatchStreamDetails(string nfoPath, TechnicalInfo? tech, long? runtimeTicks)
    {
        if (tech is null && !runtimeTicks.HasValue) return;
        if (!File.Exists(nfoPath)) return;

        var content = File.ReadAllText(nfoPath);

        // Remove existing <fileinfo> block if present
        var start = content.IndexOf("<fileinfo>", StringComparison.OrdinalIgnoreCase);
        var end   = content.IndexOf("</fileinfo>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end >= 0)
            content = content.Remove(start, end + "</fileinfo>".Length - start);

        // Build the new fileinfo block
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        using (var writer = XmlWriter.Create(ms, settings))
        {
            WriteStreamDetails(writer, tech, runtimeTicks);
            writer.Flush();
        }
        var block = "\n" + Encoding.UTF8.GetString(ms.ToArray()).Trim();

        // Insert before the closing root element
        var closeTag = content.LastIndexOf("</", StringComparison.Ordinal);
        if (closeTag < 0) return;

        content = content.Insert(closeTag, block + "\n");
        File.WriteAllText(nfoPath, content, new UTF8Encoding(false));
    }

    private static void WriteStreamDetails(XmlWriter writer, TechnicalInfo? tech, long? runtimeTicks)
    {
        // runtime from ticks if not already in metadata
        int? runtimeMinutes = null;
        if (runtimeTicks.HasValue)
            runtimeMinutes = (int)(runtimeTicks.Value / 10_000_000 / 60);

        bool hasVideo = tech is { Width: not null } or { Height: not null } or { VideoCodec: not null };
        bool hasAudio = tech is { AudioCodec: not null } or { AudioChannels: not null } or { AudioSampleRate: not null };
        bool hasRuntime = runtimeMinutes.HasValue;
        bool hasBitrate = tech?.Bitrate.HasValue ?? false;
        bool hasContainer = !string.IsNullOrEmpty(tech?.Container);

        if (!hasVideo && !hasAudio && !hasRuntime && !hasBitrate && !hasContainer) return;

        writer.WriteStartElement("fileinfo");
        writer.WriteStartElement("streamdetails");

        if (hasVideo || runtimeMinutes.HasValue)
        {
            writer.WriteStartElement("video");
            if (!string.IsNullOrEmpty(tech?.VideoCodec))
                writer.WriteElementString("codec", tech.VideoCodec);
            if (tech?.Width.HasValue == true)
                writer.WriteElementString("width", tech.Width.Value.ToString());
            if (tech?.Height.HasValue == true)
                writer.WriteElementString("height", tech.Height.Value.ToString());
            if (runtimeMinutes.HasValue)
                writer.WriteElementString("durationinseconds", ((int)(runtimeTicks!.Value / 10_000_000)).ToString());
            writer.WriteEndElement(); // video
        }

        if (hasAudio)
        {
            writer.WriteStartElement("audio");
            if (!string.IsNullOrEmpty(tech?.AudioCodec))
                writer.WriteElementString("codec", tech.AudioCodec);
            if (tech?.AudioChannels.HasValue == true)
                writer.WriteElementString("channels", tech.AudioChannels.Value.ToString());
            if (tech?.AudioSampleRate.HasValue == true)
                writer.WriteElementString("samplingrate", tech.AudioSampleRate.Value.ToString());
            writer.WriteEndElement(); // audio
        }

        writer.WriteEndElement(); // streamdetails
        writer.WriteEndElement(); // fileinfo
    }

    /// <summary>
    /// Generates the album.nfo sidecar for an audiobook item.
    /// Root element: &lt;album&gt; — read back by <see cref="VirtualLib.Providers.AudioBookNfoProvider"/>.
    /// Authors map to &lt;albumartist&gt; (primary) and &lt;artist&gt; (compat).
    /// </summary>
    public string GenerateAudioBookNfo(MediaMetadata metadata)
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

        writer.WriteEndElement(); // album (audiobook)
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
