using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualLib.Core;
using VirtualLib.Core.Cleanup;
using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Tests d'intégration légers de <see cref="SyncService"/> (#44) — comblent le gap signalé par
/// <c>code-reviewer</c> (_work/reports/code-reviewer-20260805-094419.md, "[MINEUR] Aucun test
/// n'exerce les garde-fous côté SyncService") : les garde-fous du guard rail #3 (D11, CA11) sont
/// aujourd'hui vrais par lecture du code (relu à SyncService.cs:663 pour ce lot), mais aucun test
/// ne protège contre une régression silencieuse d'un futur refactor de <c>SyncLibraryItemsAsync</c>.
///
/// Connecteur mocké directement via <see cref="IMediaServerConnector"/> (interface simple à mocker
/// avec Moq, contrairement aux tests EmbyConnector/PlexConnector qui mockent au niveau HTTP) —
/// <c>StrmGenerator</c>/<c>EpubStubGenerator</c>/<c>NfoGenerator</c> réels, écrivant dans un vrai
/// répertoire temporaire (convention projet). <c>MetadataMode.LocalScraping</c> utilisé par défaut
/// pour les scénarios qui n'ont pas besoin de le tester spécifiquement : ce mode court-circuite
/// tout appel à <c>GetMetadataAsync</c>/artwork (SyncService.cs:591-596), ce qui réduit la surface
/// de mock au strict minimum (TestConnectionAsync + ListItemsAsync).
/// </summary>
public class SyncServiceTests : IDisposable
{
    private readonly string _virtualLibRoot;

    public SyncServiceTests()
    {
        _virtualLibRoot = Path.Combine(Path.GetTempPath(), "VirtualLibSyncServiceTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_virtualLibRoot);
    }

    // -------------------------------------------------------------------------
    // Guardrail #3 (D11/CA11) : CleanupAsync jamais appelé si ItemsFailed > 0 ou 0 item
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SyncLibraryAsync_ZeroItemsReturned_DoesNotInvokeCleanup()
    {
        var connector = MockConnector(Array.Empty<MediaItem>());
        var cleanup = new Mock<ILibraryCleanupService>();
        var service = CreateSyncService(connector.Object, cleanup.Object);

        await service.SyncLibraryAsync(
            NewConfig(), "lib1", _virtualLibRoot, "http://proxy", progress: null, ct: default,
            orphanCleanupEnabled: true);

        cleanup.Verify(c => c.CleanupAsync(
                It.IsAny<string>(), It.IsAny<ISet<string>>(), It.IsAny<CleanupOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncLibraryAsync_ItemFailed_DoesNotInvokeCleanup()
    {
        // MetadataMode.RemoteSync (pas LocalScraping, pour atteindre l'appel GetMetadataAsync) +
        // échec de GetMetadataAsync → incrémente libFailed (SyncService.cs:623-627).
        var connector = MockConnector(new[] { Movie() });
        connector.Setup(c => c.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("remote metadata unavailable"));
        var cleanup = new Mock<ILibraryCleanupService>();
        var service = CreateSyncService(connector.Object, cleanup.Object);

        var (result, _) = await service.SyncLibraryAsync(
            NewConfig(MetadataMode.RemoteSync), "lib1", _virtualLibRoot, "http://proxy", progress: null,
            ct: default, orphanCleanupEnabled: true);

        Assert.True(result.ItemsFailed > 0); // précondition du test : on a bien provoqué un échec
        cleanup.Verify(c => c.CleanupAsync(
                It.IsAny<string>(), It.IsAny<ISet<string>>(), It.IsAny<CleanupOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncLibraryAsync_OrphanCleanupDisabled_DoesNotInvokeCleanup()
    {
        // orphanCleanupEnabled=false — même avec un cas par ailleurs parfaitement nominal.
        var connector = MockConnector(new[] { Movie() });
        var cleanup = new Mock<ILibraryCleanupService>();
        var service = CreateSyncService(connector.Object, cleanup.Object);

        await service.SyncLibraryAsync(
            NewConfig(), "lib1", _virtualLibRoot, "http://proxy", progress: null, ct: default,
            orphanCleanupEnabled: false);

        cleanup.Verify(c => c.CleanupAsync(
                It.IsAny<string>(), It.IsAny<ISet<string>>(), It.IsAny<CleanupOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -------------------------------------------------------------------------
    // Cas nominal : CleanupAsync appelé, DryRun forcé (SyncService.cs:673-675 — "DryRun forced
    // true for now — no persisted setting/UI to disable it yet (#12)")
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SyncLibraryAsync_NominalCase_InvokesCleanup_WithDryRunTrue()
    {
        var connector = MockConnector(new[] { Movie() });
        var cleanup = new Mock<ILibraryCleanupService>();
        cleanup.Setup(c => c.CleanupAsync(
                It.IsAny<string>(), It.IsAny<ISet<string>>(), It.IsAny<CleanupOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanupResult { Executed = true });
        var service = CreateSyncService(connector.Object, cleanup.Object);

        await service.SyncLibraryAsync(
            NewConfig(), "lib1", _virtualLibRoot, "http://proxy", progress: null, ct: default,
            orphanCleanupEnabled: true);

        cleanup.Verify(c => c.CleanupAsync(
                It.IsAny<string>(),
                It.IsAny<ISet<string>>(),
                // Peu importe le contenu hypothétique d'un futur CleanupOptions de configuration
                // (cf. remarque code-reviewer) — DryRun == true est la seule invariante testée ici.
                It.Is<CleanupOptions>(o => o.DryRun),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -------------------------------------------------------------------------
    // Bonus — CA6 : log d'avertissement agrégé fail-open avec le bon décompte
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SyncLibraryAsync_MediaFilterFailOpen_LogsAggregatedWarningWithCorrectCount()
    {
        // 2 films sans TechnicalInfo, règle de résolution active sans repli possible →
        // MediaFilterEngine.Evaluate renvoie KeptUnknownInfo pour chacun (comportement vérifié
        // isolément dans MediaFilterEngineTests, Batch 1) — SyncService doit agréger et logguer
        // le décompte exact, jamais un log par item (D7).
        var connector = MockConnector(new[] { Movie("m1"), Movie("m2") });
        var cleanup = new Mock<ILibraryCleanupService>();
        var logger = new RecordingLogger<SyncService>();
        var service = CreateSyncService(connector.Object, cleanup.Object, logger);
        var config = NewConfig();
        config.MediaFilter = new MediaFilterConfig { MinHeight = 1080 };

        await service.SyncLibraryAsync(
            config, "lib1", _virtualLibRoot, "http://proxy", progress: null, ct: default,
            orphanCleanupEnabled: false);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("2")
            && e.Message.Contains("conservés faute d'information technique"));
    }

    // -------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------

    private static ConnectorConfig NewConfig(MetadataMode mode = MetadataMode.LocalScraping) => new()
    {
        Id = "conn1",
        DisplayName = "TestConnector",
        ServerType = "Emby",
        ServerUrl = "http://test.local",
        MetadataMode = mode,
        KnownLibraries = new List<KnownLibrary> { new() { Id = "lib1", Name = "Films", Type = "Movies" } }
    };

    private static MediaItem Movie(string id = "m1") => new()
    {
        RemoteId = id,
        Title = "Test Movie " + id,
        Type = MediaType.Movie,
        Year = 2020
    };

    private static Mock<IMediaServerConnector> MockConnector(IReadOnlyList<MediaItem> items)
    {
        var mock = new Mock<IMediaServerConnector>();
        mock.Setup(c => c.TestConnectionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConnectorTestResult.Ok("1.0.0"));
        mock.Setup(c => c.ListItemsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);
        return mock;
    }

    private static SyncService CreateSyncService(
        IMediaServerConnector connector,
        ILibraryCleanupService cleanupService,
        ILogger<SyncService>? logger = null)
    {
        var factory = new Mock<IConnectorFactory>();
        factory.Setup(f => f.Create(It.IsAny<ConnectorConfig>())).Returns(connector);

        return new SyncService(
            factory.Object,
            new StrmGenerator(),
            new EpubStubGenerator(),
            new NfoGenerator(),
            logger ?? NullLogger<SyncService>.Instance,
            libraryCleanupService: cleanupService);
    }

    /// <summary>ILogger de test qui capture les messages formatés — évite de mocker ILogger&lt;T&gt;.Log via Moq.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_virtualLibRoot))
            Directory.Delete(_virtualLibRoot, recursive: true);
    }
}
