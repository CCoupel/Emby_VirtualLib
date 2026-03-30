using VirtualLib.Core;
using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

public class StrmGeneratorTests : IDisposable
{
    private readonly StrmGenerator _generator = new();
    private readonly string _tempDir;

    public StrmGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "VirtualLibTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Generate_Movie_Creates_Correct_Path_And_Url()
    {
        var item = new MediaItem
        {
            RemoteId = "12345",
            Title = "Inception",
            Type = MediaType.Movie,
            Year = 2010
        };

        var path = _generator.Generate(item, "emby-b", "Films", _tempDir);

        Assert.True(File.Exists(path));
        Assert.EndsWith(".strm", path);
        Assert.Contains("Inception (2010)", path);

        var content = File.ReadAllText(path);
        Assert.Equal("http://localhost:8096/virtuallib/proxy/emby-b/12345", content);
    }

    [Fact]
    public void Generate_Episode_Creates_Correct_Season_Path()
    {
        var item = new MediaItem
        {
            RemoteId = "67890",
            Title = "Pilot",
            Type = MediaType.Episode,
            SeriesName = "Breaking Bad",
            SeasonNumber = 1,
            EpisodeNumber = 1
        };

        var path = _generator.Generate(item, "emby-b", "Séries", _tempDir);

        Assert.True(File.Exists(path));
        Assert.Contains("Breaking Bad", path);
        Assert.Contains("Season 01", path);
        Assert.EndsWith("S01E01.strm", path);
    }

    [Fact]
    public void Generate_Uses_Custom_ProxyBaseUrl()
    {
        var item = new MediaItem
        {
            RemoteId = "999",
            Title = "Test Movie",
            Type = MediaType.Movie,
            Year = 2023
        };

        var path = _generator.Generate(item, "conn1", "Films", _tempDir, "http://myserver:9090");
        var content = File.ReadAllText(path);

        Assert.StartsWith("http://myserver:9090/virtuallib/proxy/conn1/999", content);
    }

    [Fact]
    public void SanitizeName_Removes_Invalid_Chars()
    {
        var result = StrmGenerator.SanitizeName("Movie: The <Film> / Test");
        Assert.DoesNotContain(":", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void GetFileName_Movie_Without_Year()
    {
        var item = new MediaItem { Title = "Unknown Movie", Type = MediaType.Movie };
        var name = _generator.GetFileName(item);
        Assert.Equal("Unknown Movie", name);
    }

    [Fact]
    public void GetFileName_Episode_Formats_S00E00()
    {
        var item = new MediaItem
        {
            Title = "Episode Title",
            Type = MediaType.Episode,
            SeriesName = "My Show",
            SeasonNumber = 2,
            EpisodeNumber = 12
        };
        var name = _generator.GetFileName(item);
        Assert.Equal("My Show - S02E12", name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
