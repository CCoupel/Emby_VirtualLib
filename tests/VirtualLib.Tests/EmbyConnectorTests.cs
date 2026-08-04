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

        // Photo/HomeVideo support added in v1.4.0 — "photos" now maps to LibraryType.Photos
        // instead of being filtered out as Unknown (CHANGELOG v1.4.0).
        Assert.Equal(3, result.Count);
        Assert.Equal(LibraryType.Movies, result[0].Type);
        Assert.Equal(LibraryType.TvShows, result[1].Type);
        Assert.Equal(LibraryType.Photos, result[2].Type);
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

                // GetUserIdAsync (ApiKey mode) resolves the user via Users?IsAdministrator=true&Limit=1
                // since v1.3.0 — /Users/Me was dropped (500 error on some Emby versions, see CHANGELOG).
                if (req.RequestUri!.PathAndQuery.Contains("IsAdministrator"))
                {
                    json = JsonSerializer.Serialize(new[] { new { Id = "user1" } });
                }
                // ListItemsAsync only requests a second page when totalCount > PageSize (100,
                // EmbyConnector.cs:13) — TotalRecordCount must exceed 100 for the StartIndex=100
                // branch below to actually be reached (fixes a second, pre-existing bug in this
                // test: with TotalRecordCount=2 the pagination loop never ran a second request).
                else if (req.RequestUri.Query.Contains("StartIndex=0"))
                {
                    json = JsonSerializer.Serialize(new
                    {
                        Items = new[] { new { Id = "1", Name = "Movie1", Type = "Movie", ProductionYear = 2020 } },
                        TotalRecordCount = 101,
                        StartIndex = 0
                    });
                }
                else
                {
                    json = JsonSerializer.Serialize(new
                    {
                        Items = new[] { new { Id = "2", Name = "Movie2", Type = "Movie", ProductionYear = 2021 } },
                        TotalRecordCount = 101,
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
                // GetUserIdAsync (ApiKey mode) resolves the user via Users?IsAdministrator=true&Limit=1
                // since v1.3.0 — /Users/Me was dropped (500 error on some Emby versions, see CHANGELOG).
                if (req.RequestUri!.PathAndQuery.Contains("IsAdministrator"))
                    json = JsonSerializer.Serialize(new[] { new { Id = "user1" } });
                else
                    json = JsonSerializer.Serialize(new
                    {
                        // "Photo" is a recognized MediaType since v1.4.0 (MapItem, EmbyConnector.cs:621)
                        // — no longer a valid example of an unmapped type. Use an Emby item type absent
                        // from the MapItem switch (falls into the `_ => null` skip branch) instead.
                        Items = new[]
                        {
                            new { Id = "1", Name = "Movie1", Type = "Movie" },
                            new { Id = "2", Name = "Trailer1", Type = "Trailer" }
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

    // -------------------------------------------------------------------------
    // #44 — MapTechnicalInfo : liste complète des pistes (Streams) + non-régression scalaire (D1)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListItemsAsync_Maps_Full_Stream_List_And_Preserves_Historical_Scalars()
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
                if (req.RequestUri!.PathAndQuery.Contains("IsAdministrator"))
                {
                    json = JsonSerializer.Serialize(new[] { new { Id = "user1" } });
                }
                else
                {
                    json = JsonSerializer.Serialize(new
                    {
                        Items = new[]
                        {
                            new
                            {
                                Id = "1",
                                Name = "Inception",
                                Type = "Movie",
                                ProductionYear = 2010,
                                MediaSources = new[]
                                {
                                    new
                                    {
                                        Size = 8_000_000_000L,
                                        Bitrate = 12_000_000,
                                        Container = "mkv",
                                        MediaStreams = new object[]
                                        {
                                            new { Type = "Video", Codec = "hevc", Width = 3840, Height = 2160, Index = 0 },
                                            new { Type = "Audio", Codec = "ac3", Channels = 6, Language = "eng", IsDefault = true, Index = 1 },
                                            new { Type = "Audio", Codec = "dts", Channels = 6, Language = "fre", IsDefault = false, Index = 2 },
                                            new { Type = "Subtitle", Codec = "subrip", Language = "fre", IsForced = true, IsExternal = false, Index = 3 }
                                        }
                                    }
                                }
                            }
                        },
                        TotalRecordCount = 1,
                        StartIndex = 0
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

        using var connector = CreateConnector(mock.Object);
        var items = await connector.ListItemsAsync("lib1");

        var item = Assert.Single(items);
        Assert.NotNull(item.Technical);

        // Non-régression D1 : les scalaires historiques restent alimentés depuis la première
        // piste vidéo / première piste audio, indépendamment du contenu de la liste complète.
        Assert.Equal(2160, item.Technical!.Height);
        Assert.Equal(3840, item.Technical.Width);
        Assert.Equal("hevc", item.Technical.VideoCodec);
        Assert.Equal("ac3", item.Technical.AudioCodec); // première piste audio (eng), pas dts
        Assert.Equal(6, item.Technical.AudioChannels);

        // Liste complète produite (#44) : les 4 pistes, dans l'ordre du conteneur.
        Assert.Equal(4, item.Technical.Streams.Count);
        Assert.Equal(MediaStreamKind.Video, item.Technical.Streams[0].Kind);
        Assert.Equal(MediaStreamKind.Audio, item.Technical.Streams[1].Kind);
        Assert.Equal(MediaStreamKind.Audio, item.Technical.Streams[2].Kind);
        Assert.Equal(MediaStreamKind.Subtitle, item.Technical.Streams[3].Kind);

        // Deuxième piste audio (française, "fre") bien présente dans la liste et normalisée en "fra".
        Assert.Equal("fra", item.Technical.Streams[2].LanguageCode);
        Assert.True(item.Technical.Streams[3].IsForced);
    }

    [Fact]
    public async Task ListItemsAsync_Item_Without_MediaSources_Has_Null_Technical()
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
                if (req.RequestUri!.PathAndQuery.Contains("IsAdministrator"))
                    json = JsonSerializer.Serialize(new[] { new { Id = "user1" } });
                else
                    json = JsonSerializer.Serialize(new
                    {
                        Items = new[] { new { Id = "1", Name = "NoTech", Type = "Movie", ProductionYear = 2020 } },
                        TotalRecordCount = 1,
                        StartIndex = 0
                    });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

        using var connector = CreateConnector(mock.Object);
        var items = await connector.ListItemsAsync("lib1");

        // Aucun MediaSources dans la réponse : Technical doit rester null — c'est cette absence
        // que MediaFilterEngine (#44) traite en fail-open (KeptUnknownInfo), pas une exception.
        var item = Assert.Single(items);
        Assert.Null(item.Technical);
    }

    [Fact]
    public async Task GetStreamInfoAsync_Emby_Always_Returns_Empty_Dictionary_Without_Network_Call()
    {
        // D5 : Emby fournit déjà toutes les pistes via MediaSources dans ListItemsAsync — aucun
        // appel réseau supplémentaire n'est nécessaire. Vérifié en n'enregistrant aucune réponse
        // sur SendAsync et en s'assurant qu'il n'a jamais été invoqué (Dispose() du HttpClient
        // n'est pas concerné — seul SendAsync compte comme "appel réseau").
        var mock = new Mock<HttpMessageHandler>();
        using var connector = CreateConnector(mock.Object);

        var result = await connector.GetStreamInfoAsync(new[] { "1", "2", "3" });

        Assert.Empty(result);
        mock.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }
}
