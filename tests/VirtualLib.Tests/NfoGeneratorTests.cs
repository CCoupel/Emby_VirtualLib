using System.Xml.Linq;
using VirtualLib.Core;
using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

public class NfoGeneratorTests : IDisposable
{
    private readonly NfoGenerator _generator = new();
    private readonly string _tempDir;

    public NfoGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "VirtualLibNfoTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void GenerateMovieNfo_Contains_All_Fields()
    {
        var metadata = new MediaMetadata
        {
            RemoteId = "1",
            Title = "Inception",
            Type = MediaType.Movie,
            Year = 2010,
            Overview = "A thief who steals corporate secrets",
            CommunityRating = 8.8f,
            RuntimeMinutes = 148,
            Genres = new[] { "Science-Fiction", "Thriller" },
            Studios = new[] { "Warner Bros." },
            ImdbId = "tt1375666",
            TmdbId = "27205",
            OfficialRating = "PG-13"
        };

        var xml = _generator.GenerateMovieNfo(metadata);
        var doc = XDocument.Parse(xml);

        Assert.Equal("Inception", doc.Root!.Element("title")?.Value);
        Assert.Equal("2010", doc.Root.Element("year")?.Value);
        Assert.Equal("A thief who steals corporate secrets", doc.Root.Element("plot")?.Value);
        Assert.Equal("8.8", doc.Root.Element("rating")?.Value);
        Assert.Equal("148", doc.Root.Element("runtime")?.Value);
        Assert.Equal("PG-13", doc.Root.Element("mpaa")?.Value);
        Assert.Contains(doc.Root.Elements("genre"), e => e.Value == "Science-Fiction");
        Assert.Contains(doc.Root.Elements("studio"), e => e.Value == "Warner Bros.");

        var imdbId = doc.Root.Elements("uniqueid").FirstOrDefault(e => e.Attribute("type")?.Value == "imdb");
        Assert.Equal("tt1375666", imdbId?.Value);

        var tmdbId = doc.Root.Elements("uniqueid").FirstOrDefault(e => e.Attribute("type")?.Value == "tmdb");
        Assert.Equal("27205", tmdbId?.Value);
    }

    [Fact]
    public void GenerateMovieNfo_Root_Element_Is_Movie()
    {
        var metadata = new MediaMetadata
        {
            RemoteId = "1",
            Title = "Test",
            Type = MediaType.Movie
        };

        var xml = _generator.GenerateMovieNfo(metadata);
        var doc = XDocument.Parse(xml);

        Assert.Equal("movie", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void GenerateEpisodeNfo_Contains_Series_Info()
    {
        var metadata = new MediaMetadata
        {
            RemoteId = "2",
            Title = "Pilot",
            Type = MediaType.Episode,
            SeriesName = "Breaking Bad",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Overview = "A chemistry teacher turns to crime",
            CommunityRating = 9.0f,
            TvdbId = "81189"
        };

        var xml = _generator.GenerateEpisodeNfo(metadata);
        var doc = XDocument.Parse(xml);

        Assert.Equal("episodedetails", doc.Root!.Name.LocalName);
        Assert.Equal("Pilot", doc.Root.Element("title")?.Value);
        Assert.Equal("Breaking Bad", doc.Root.Element("showtitle")?.Value);
        Assert.Equal("1", doc.Root.Element("season")?.Value);
        Assert.Equal("1", doc.Root.Element("episode")?.Value);

        var tvdbId = doc.Root.Elements("uniqueid").FirstOrDefault(e => e.Attribute("type")?.Value == "tvdb");
        Assert.Equal("81189", tvdbId?.Value);
    }

    [Fact]
    public void Generate_Creates_File_For_Movie()
    {
        var metadata = new MediaMetadata
        {
            RemoteId = "1",
            Title = "Inception",
            Type = MediaType.Movie,
            Year = 2010
        };

        var path = _generator.Generate(metadata, _tempDir);

        Assert.True(File.Exists(path));
        Assert.EndsWith(".nfo", path);
        Assert.Contains("Inception", path);
    }

    [Fact]
    public void Generate_Returns_Empty_For_Unsupported_Type()
    {
        var metadata = new MediaMetadata
        {
            RemoteId = "1",
            Title = "Photo",
            Type = MediaType.Photo
        };

        var path = _generator.Generate(metadata, _tempDir);

        Assert.Equal(string.Empty, path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
