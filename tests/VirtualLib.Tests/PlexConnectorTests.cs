using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using VirtualLib.Connectors;
using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Tests unitaires de <see cref="PlexConnector"/> — mappers XML figés (#44, tâche 9 :
/// ParseTechnicalInfo / Streams) et récupération groupée des pistes par lots (#44, tâche 8 :
/// GetStreamInfoAsync, D6). Aucun appel réseau réel — HttpMessageHandler mocké, conformément
/// aux conventions du projet (CLAUDE.md).
///
/// Les fixtures XML utilisent des attributs entre apostrophes (XML valide) pour rester des
/// chaînes verbatim C# sans échappement de guillemets.
/// </summary>
public class PlexConnectorTests
{
    private static PlexConnector CreateConnector(HttpMessageHandler handler, string libraryId = "lib1")
    {
        var config = new ConnectorConfig
        {
            Id = "test-plex-connector",
            DisplayName = "Test Plex Server",
            ServerType = "Plex",
            ServerUrl = "http://plex.test:32400",
            AuthMode = AuthMode.ApiKey,
            ApiKey = "test-plex-token",
            // Fast path pour GetSectionTypeAsync — évite un aller-retour réseau supplémentaire
            // vers library/sections dans les tests qui ne portent pas dessus.
            KnownLibraries = new List<KnownLibrary>
            {
                new() { Id = libraryId, Name = "Films", Type = "Movies" }
            }
        };

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        return new PlexConnector(config, mockFactory.Object, NullLogger<PlexConnector>.Instance);
    }

    private static HttpMessageHandler MockHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => respond(req));
        return mock.Object;
    }

    private static HttpResponseMessage XmlResponse(HttpStatusCode status, string xml) => new(status)
    {
        Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
    };

    // -------------------------------------------------------------------------
    // ListItemsAsync / MapVideoToItem → ParseTechnicalInfo → Streams (#44 tâche 9)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListItemsAsync_Maps_Video_Streams_And_Preserves_Media_Scalars()
    {
        // <Stream> avec seulement languageTag ('fr') + libellé ('Français'), sans languageCode :
        // vérifie que MapPlexStream fait converger les trois champs de langue Plex vers LanguageMatcher
        // (D1 point d'attention), pas seulement le cas trivial où languageCode est déjà 'fra'.
        const string xml = @"
            <MediaContainer totalSize='1'>
              <Video ratingKey='1' title='Inception' type='movie' year='2010'>
                <Media container='mkv' videoCodec='hevc' audioCodec='dts' width='3840' height='2160' bitrate='12000'>
                  <Part size='8000000000'>
                    <Stream streamType='1' codec='hevc' index='0' />
                    <Stream streamType='2' codec='dts' channels='6' language='English' languageCode='eng' languageTag='en' default='1' index='1' />
                    <Stream streamType='2' codec='ac3' channels='6' language='Français' languageTag='fr' index='2' />
                    <Stream streamType='3' codec='srt' language='Français' languageTag='fr' forced='1' index='3' />
                  </Part>
                </Media>
              </Video>
            </MediaContainer>";

        var handler = MockHandler(req => XmlResponse(HttpStatusCode.OK, xml));
        using var connector = CreateConnector(handler);

        var items = await connector.ListItemsAsync("lib1");

        var item = Assert.Single(items);
        Assert.NotNull(item.Technical);

        // Scalaires historiques — inchangés, lus directement sur <Media> (non-régression).
        Assert.Equal("mkv", item.Technical!.Container);
        Assert.Equal("hevc", item.Technical.VideoCodec);
        Assert.Equal("dts", item.Technical.AudioCodec);
        Assert.Equal(2160, item.Technical.Height);
        Assert.Equal(3840, item.Technical.Width);

        // Liste complète des 4 pistes (#44).
        Assert.Equal(4, item.Technical.Streams.Count);
        Assert.Equal(MediaStreamKind.Video, item.Technical.Streams[0].Kind);
        Assert.Equal(MediaStreamKind.Audio, item.Technical.Streams[1].Kind);
        Assert.Equal(MediaStreamKind.Audio, item.Technical.Streams[2].Kind);
        Assert.Equal(MediaStreamKind.Subtitle, item.Technical.Streams[3].Kind);

        // Convergence des 3 champs de langue Plex (languageCode/languageTag/language) vers "fra",
        // y compris quand seul languageTag ("fr") est présent (2e piste audio, 3e élément).
        Assert.Equal("fra", item.Technical.Streams[2].LanguageCode);
        Assert.True(item.Technical.Streams[3].IsForced);
        Assert.False(item.Technical.Streams[3].IsExternal); // pas d'attribut "key" → piste embarquée
    }

    [Fact]
    public async Task ListItemsAsync_Video_Without_Media_Element_Has_Null_Technical()
    {
        const string xml = @"
            <MediaContainer totalSize='1'>
              <Video ratingKey='1' title='No Media Info' type='movie' year='2021' />
            </MediaContainer>";

        var handler = MockHandler(req => XmlResponse(HttpStatusCode.OK, xml));
        using var connector = CreateConnector(handler);

        var items = await connector.ListItemsAsync("lib1");

        var item = Assert.Single(items);
        Assert.Null(item.Technical);
    }

    [Fact]
    public async Task ListItemsAsync_Media_With_Streams_Only_NoOtherAttributes_Still_Has_NonNull_Technical()
    {
        // <Media> sans aucun attribut technique classique (container/codec/width/height/bitrate)
        // mais avec des <Stream> réels : Technical doit rester non-null grâce à Streams.Count > 0
        // (tâche 9 — sans ce garde-fou, hasAny serait faux et l'info technique serait perdue).
        const string xml = @"
            <MediaContainer totalSize='1'>
              <Video ratingKey='1' title='Streams Only' type='movie' year='2022'>
                <Media>
                  <Part>
                    <Stream streamType='2' codec='aac' language='English' languageCode='eng' index='0' />
                  </Part>
                </Media>
              </Video>
            </MediaContainer>";

        var handler = MockHandler(req => XmlResponse(HttpStatusCode.OK, xml));
        using var connector = CreateConnector(handler);

        var items = await connector.ListItemsAsync("lib1");

        var item = Assert.Single(items);
        Assert.NotNull(item.Technical);
        Assert.Single(item.Technical!.Streams);
        Assert.Equal(MediaStreamKind.Audio, item.Technical.Streams[0].Kind);
    }

    [Fact]
    public async Task ListItemsAsync_Media_Without_Part_Has_Empty_Streams_But_NonNull_Technical()
    {
        const string xml = @"
            <MediaContainer totalSize='1'>
              <Video ratingKey='1' title='No Part' type='movie' year='2023'>
                <Media container='mp4' videoCodec='h264' />
              </Video>
            </MediaContainer>";

        var handler = MockHandler(req => XmlResponse(HttpStatusCode.OK, xml));
        using var connector = CreateConnector(handler);

        var items = await connector.ListItemsAsync("lib1");

        var item = Assert.Single(items);
        Assert.NotNull(item.Technical);
        Assert.Empty(item.Technical!.Streams);
    }

    // -------------------------------------------------------------------------
    // GetStreamInfoAsync — récupération groupée par lots (#44 tâche 8, D6)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetStreamInfoAsync_SingleBatch_Requests_Comma_Joined_Ids_In_One_Call()
    {
        var requestedUrls = new ConcurrentBag<string>();
        const string xml = @"
            <MediaContainer>
              <Video ratingKey='1'><Media><Part><Stream streamType='2' codec='aac' languageCode='eng' /></Part></Media></Video>
              <Video ratingKey='2'><Media><Part><Stream streamType='2' codec='ac3' languageCode='fra' /></Part></Media></Video>
              <Video ratingKey='3'><Media><Part /></Media></Video>
            </MediaContainer>";

        var handler = MockHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.PathAndQuery);
            return XmlResponse(HttpStatusCode.OK, xml);
        });
        using var connector = CreateConnector(handler);

        var result = await connector.GetStreamInfoAsync(new[] { "1", "2", "3" });

        var url = Assert.Single(requestedUrls);
        Assert.Equal("/library/metadata/1,2,3", url);

        Assert.Equal(3, result.Count);
        Assert.Single(result["1"]);
        Assert.Equal("eng", result["1"][0].LanguageCode);
        Assert.Equal("fra", result["2"][0].LanguageCode);
        Assert.Empty(result["3"]); // <Part/> vide → liste de pistes vide, mais clé bien présente
    }

    [Fact]
    public async Task GetStreamInfoAsync_Splits_Into_Batches_Of_PlexStreamBatchSize()
    {
        // 120 ids, taille de lot 50 (PlexStreamBatchSize, PlexConnector.cs:17) → ceil(120/50) = 3 lots.
        var ids = Enumerable.Range(1, 120).Select(i => i.ToString()).ToList();
        var requestedIdCounts = new ConcurrentBag<int>();

        var handler = MockHandler(req =>
        {
            var idsInUrl = req.RequestUri!.PathAndQuery
                .Replace("/library/metadata/", string.Empty)
                .Split(',');
            requestedIdCounts.Add(idsInUrl.Length);
            return XmlResponse(HttpStatusCode.OK, "<MediaContainer></MediaContainer>");
        });
        using var connector = CreateConnector(handler);

        await connector.GetStreamInfoAsync(ids);

        Assert.Equal(3, requestedIdCounts.Count);
        Assert.Equal(new[] { 20, 50, 50 }, requestedIdCounts.OrderBy(c => c));
    }

    [Fact]
    public async Task GetStreamInfoAsync_RetriesAsTwoHalves_OnBatch4xxFailure_ThenMerges()
    {
        // Le lot combiné [1,2] échoue en 400 → doit être re-tenté récursivement en deux lots de
        // taille 1 ([1] et [2]), chacun réussissant individuellement — résultats fusionnés (D6).
        var calls = new ConcurrentBag<string>();

        var handler = MockHandler(req =>
        {
            var idsPart = req.RequestUri!.PathAndQuery.Replace("/library/metadata/", string.Empty);
            calls.Add(idsPart);

            if (idsPart == "1,2")
                return new HttpResponseMessage(HttpStatusCode.BadRequest);

            var ratingKey = idsPart; // lot de taille 1 : "1" ou "2"
            var xml = $@"
                <MediaContainer>
                  <Video ratingKey='{ratingKey}'><Media><Part>
                    <Stream streamType='2' codec='aac' languageCode='eng' />
                  </Part></Media></Video>
                </MediaContainer>";
            return XmlResponse(HttpStatusCode.OK, xml);
        });
        using var connector = CreateConnector(handler);

        var result = await connector.GetStreamInfoAsync(new[] { "1", "2" });

        Assert.Contains("1,2", calls);   // tentative initiale en un seul lot
        Assert.Contains("1", calls);     // retry en deux moitiés...
        Assert.Contains("2", calls);     // ...taille 1 chacune
        Assert.Equal(3, calls.Count);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("1"));
        Assert.True(result.ContainsKey("2"));
    }

    [Fact]
    public async Task GetStreamInfoAsync_BatchOfOne_FailingWith4xx_IsAbsentFromResult_FailOpen()
    {
        // Un seul id, échec 4xx : plus de subdivision possible (taille 1) — l'item est absent du
        // dictionnaire retourné, ce qui signifie "information indisponible" côté SyncService (D7),
        // pas une exception qui remonterait jusqu'à l'appelant.
        var handler = MockHandler(req => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var connector = CreateConnector(handler);

        var exception = await Record.ExceptionAsync(() => connector.GetStreamInfoAsync(new[] { "1" }));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetStreamInfoAsync_BatchOfOne_FailingWith4xx_ReturnsEmptyDictionary()
    {
        var handler = MockHandler(req => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var connector = CreateConnector(handler);

        var result = await connector.GetStreamInfoAsync(new[] { "1" });

        Assert.Empty(result);
        Assert.False(result.ContainsKey("1"));
    }

    [Fact]
    public async Task GetStreamInfoAsync_EmptyIdList_ReturnsEmptyDictionary_WithoutAnyCall()
    {
        var handler = new Mock<HttpMessageHandler>();
        using var connector = CreateConnector(handler.Object);

        var result = await connector.GetStreamInfoAsync(Array.Empty<string>());

        Assert.Empty(result);
        handler.Protected().Verify(
            "SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}
