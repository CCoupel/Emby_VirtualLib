using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using VirtualLib.Connectors;
using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

public class EmbyConnectorTests
{
    private static EmbyConnector CreateConnector(HttpMessageHandler handler)
    {
        var config = new ConnectorConfig
        {
            Id = "test-connector",
            DisplayName = "Test Server",
            ServerType = "Emby",
            ServerUrl = "http://emby.test",
            ApiKey = "test-api-key"
        };

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        return new EmbyConnector(config, mockFactory.Object, NullLogger<EmbyConnector>.Instance);
    }

    private static HttpMessageHandler MockHandler(string path, HttpStatusCode status, object? body = null)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                var json = body is not null ? JsonSerializer.Serialize(body) : "{}";
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });
        return mock.Object;
    }

    [Fact]
    public async Task TestConnectionAsync_Returns_Ok_On_Success()
    {
        var handler = MockHandler("/emby/System/Info/Public", HttpStatusCode.OK, new { Version = "4.8.0" });
        using var connector = CreateConnector(handler);

        var result = await connector.TestConnectionAsync();

        Assert.True(result.Success);
        Assert.Equal("4.8.0", result.ServerVersion);
    }

    [Fact]
    public async Task TestConnectionAsync_Returns_Fail_On_HttpError()
    {
        var handler = MockHandler("/emby/System/Info/Public", HttpStatusCode.Unauthorized);
        using var connector = CreateConnector(handler);

        var result = await connector.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ListLibrariesAsync_Maps_CollectionType_Correctly()
    {
        var libraries = new[]
        {
            new { ItemId = "lib1", Name = "Films", CollectionType = "movies" },
            new { ItemId = "lib2", Name = "Séries", CollectionType = "tvshows" },
            new { ItemId = "lib3", Name = "Photos", CollectionType = "photos" }
        };

        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(libraries),
                    System.Text.Encoding.UTF8,
                    "application/json")
            });

        using var connector = CreateConnector(mock.Object);
        var result = await connector.ListLibrariesAsync();

        // Photos (Unknown) filtered out
        Assert.Equal(2, result.Count);
        Assert.Equal(LibraryType.Movies, result[0].Type);
        Assert.Equal(LibraryType.TvShows, result[1].Type);
    }

    [Fact]
    public async Task GetStreamUrlAsync_Returns_Static_Url()
    {
        var handler = MockHandler("", HttpStatusCode.OK);
        using var connector = CreateConnector(handler);

        var url = await connector.GetStreamUrlAsync("12345");

        Assert.Contains("/Videos/12345/stream", url);
        Assert.Contains("Static=true", url);
        Assert.Contains("api_key=test-api-key", url);
    }

    [Fact]
    public async Task GetArtworkStreamAsync_Returns_Null_On_404()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        using var connector = CreateConnector(mock.Object);
        var stream = await connector.GetArtworkStreamAsync("12345", ArtworkType.Poster);

        Assert.Null(stream);
    }

    [Fact]
    public async Task ListItemsAsync_Handles_Pagination()
    {
        var callCount = 0;
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                callCount++;
                string json;

                if (req.RequestUri!.PathAndQuery.Contains("Users/Me"))
                {
                    json = JsonSerializer.Serialize(new { Id = "user1" });
                }
                else if (req.RequestUri.Query.Contains("StartIndex=0"))
                {
                    json = JsonSerializer.Serialize(new
                    {
                        Items = new[] { new { Id = "1", Name = "Movie1", Type = "Movie", ProductionYear = 2020 } },
                        TotalRecordCount = 2,
                        StartIndex = 0
                    });
                }
                else
                {
                    json = JsonSerializer.Serialize(new
                    {
                        Items = new[] { new { Id = "2", Name = "Movie2", Type = "Movie", ProductionYear = 2021 } },
                        TotalRecordCount = 2,
                        StartIndex = 100
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

        using var connector = CreateConnector(mock.Object);
        var items = await connector.ListItemsAsync("lib1");

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task ListItemsAsync_Skips_Unknown_MediaTypes()
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                string json;
                if (req.RequestUri!.PathAndQuery.Contains("Users/Me"))
                    json = JsonSerializer.Serialize(new { Id = "user1" });
                else
                    json = JsonSerializer.Serialize(new
                    {
                        Items = new[]
                        {
                            new { Id = "1", Name = "Movie1", Type = "Movie" },
                            new { Id = "2", Name = "Photo1", Type = "Photo" }
                        },
                        TotalRecordCount = 2,
                        StartIndex = 0
                    });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

        using var connector = CreateConnector(mock.Object);
        var items = await connector.ListItemsAsync("lib1");

        Assert.Single(items);
        Assert.Equal(MediaType.Movie, items[0].Type);
    }
}
