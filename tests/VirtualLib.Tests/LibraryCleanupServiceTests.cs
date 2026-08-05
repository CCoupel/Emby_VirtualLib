using Microsoft.Extensions.Logging.Abstractions;
using VirtualLib.Core.Cleanup;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Tests unitaires de <see cref="LibraryCleanupService"/> (#44, plan D11 —
/// _work/reports/planner-20260804-164559.md). Brique partagée avec #12, livrée désactivée par
/// défaut. Convention projet (cf. StrmGeneratorTests/NfoGeneratorTests) : opérations fichiers
/// exercées sur un vrai répertoire temporaire, pas de mock d'un IFileSystem.
///
/// Garde-fous CA11 tels qu'effectivement implémentés (relu depuis
/// src/VirtualLib/Core/Cleanup/LibraryCleanupService.cs) :
/// - <c>CleanupOptions.Enabled == false</c> → abandon immédiat, aucun accès disque.
/// - <c>expectedPaths</c> vide → abandon (filet de sécurité). C'est le mécanisme par lequel le
///   3e garde-fou du plan ("abandon si la sync a échoué ou retourné 0 item") s'applique : c'est
///   à <c>SyncService</c> de ne PAS peupler <c>expectedPaths</c> (ou de passer un ensemble vide)
///   dans ces cas — <c>CleanupOptions</c> lui-même n'a pas de champ dédié pour ça, le filet de
///   sécurité côté service est la protection réelle si l'appelant l'omettait par erreur.
/// - Dossier <c>libraryFolderPath</c> absent → PAS un abandon (<c>Executed = true</c>), traité
///   comme "rien à nettoyer" (aucun fichier ne peut être orphelin dans un dossier qui n'existe pas).
/// </summary>
public class LibraryCleanupServiceTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly ILibraryCleanupService _service = new LibraryCleanupService(NullLogger<LibraryCleanupService>.Instance);

    public LibraryCleanupServiceTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), "VirtualLibCleanupTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_libraryRoot);
    }

    // -------------------------------------------------------------------------
    // CleanupOptions — valeurs par défaut sûres (D11)
    // -------------------------------------------------------------------------

    [Fact]
    public void CleanupOptions_Defaults_Are_Safe_By_Construction()
    {
        var options = new CleanupOptions();

        Assert.False(options.Enabled); // jamais actif tant que l'utilisateur ne l'a pas activé
        Assert.True(options.DryRun);   // journalise seulement, à la première activation
    }

    // -------------------------------------------------------------------------
    // Garde-fous CA11
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CleanupAsync_Disabled_DoesNotDeleteAnything()
    {
        var orphan = CreateFile("Old Movie (2019)/Old Movie (2019).strm", "http://stale/url");

        var result = await _service.CleanupAsync(
            _libraryRoot,
            expectedPaths: new HashSet<string>(),
            options: new CleanupOptions { Enabled = false, DryRun = false });

        Assert.False(result.Executed);
        Assert.NotNull(result.AbortReason);
        Assert.True(File.Exists(orphan)); // rien touché — le service ne doit même pas scanner le disque
    }

    [Fact]
    public async Task CleanupAsync_EmptyExpectedPaths_AbandonsWithoutDeleting()
    {
        // Filet de sécurité (D11 point 3) : un ensemble de chemins attendus vide n'est presque
        // jamais légitime (sync échouée, 0 item retourné) — abandon inconditionnel plutôt que
        // d'effacer tout le contenu du dossier.
        var orphan = CreateFile("Old Movie (2019)/Old Movie (2019).strm", "http://stale/url");

        var result = await _service.CleanupAsync(
            _libraryRoot,
            expectedPaths: new HashSet<string>(),
            options: new CleanupOptions { Enabled = true, DryRun = false });

        Assert.False(result.Executed);
        Assert.NotNull(result.AbortReason);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task CleanupAsync_MissingLibraryFolder_IsNotAnAbort_ButDeletesNothing()
    {
        var missingFolder = Path.Combine(_libraryRoot, "does-not-exist");

        var result = await _service.CleanupAsync(
            missingFolder,
            expectedPaths: new HashSet<string> { Path.Combine(missingFolder, "whatever.strm") },
            options: new CleanupOptions { Enabled = true, DryRun = false });

        Assert.True(result.Executed); // pas un abandon : rien à nettoyer, pas une anomalie
        Assert.Empty(result.OrphansFound);
        Assert.Empty(result.OrphansDeleted);
    }

    // -------------------------------------------------------------------------
    // DryRun
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CleanupAsync_DryRun_IdentifiesOrphan_ButDoesNotDeleteIt()
    {
        var orphan = CreateFile("Removed Movie (2018)/Removed Movie (2018).strm", "http://stale/url");
        var kept = CreateFile("Kept Movie (2020)/Kept Movie (2020).strm", "http://ok/url");

        var result = await _service.CleanupAsync(
            _libraryRoot,
            expectedPaths: new HashSet<string> { kept },
            options: new CleanupOptions { Enabled = true, DryRun = true });

        Assert.True(result.Executed);
        Assert.True(File.Exists(orphan)); // DryRun : identifié, jamais supprimé
        Assert.True(File.Exists(kept));
        Assert.Contains(orphan, result.OrphansFound);
        Assert.Empty(result.OrphansDeleted); // jamais de suppression réelle en DryRun
    }

    // -------------------------------------------------------------------------
    // Suppression réelle (Enabled + DryRun=false)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CleanupAsync_DeletesOrphan_WhenEnabledAndNotDryRun()
    {
        var orphan = CreateFile("Removed Movie (2018)/Removed Movie (2018).strm", "http://stale/url");
        var kept = CreateFile("Kept Movie (2020)/Kept Movie (2020).strm", "http://ok/url");

        var result = await _service.CleanupAsync(
            _libraryRoot,
            expectedPaths: new HashSet<string> { kept },
            options: new CleanupOptions { Enabled = true, DryRun = false });

        Assert.True(result.Executed);
        Assert.False(File.Exists(orphan)); // supprimé
        Assert.Contains(orphan, result.OrphansFound);
        Assert.Contains(orphan, result.OrphansDeleted);
    }

    [Fact]
    public async Task CleanupAsync_NeverDeletes_ExpectedFile()
    {
        // Un fichier présent sur disque ET dans expectedPaths ne doit jamais être touché, même
        // en présence d'autres orphelins traités dans le même appel.
        var kept = CreateFile("Kept Movie (2020)/Kept Movie (2020).strm", "http://ok/url");
        var keptNfo = CreateFile("Kept Movie (2020)/movie.nfo", "<movie></movie>");
        var orphan = CreateFile("Removed Movie (2018)/Removed Movie (2018).strm", "http://stale/url");

        var result = await _service.CleanupAsync(
            _libraryRoot,
            expectedPaths: new HashSet<string> { kept, keptNfo },
            options: new CleanupOptions { Enabled = true, DryRun = false });

        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(keptNfo));
        Assert.DoesNotContain(kept, result.OrphansFound);
        Assert.DoesNotContain(keptNfo, result.OrphansFound);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task CleanupAsync_NoOrphans_DeletesNothing()
    {
        var kept = CreateFile("Kept Movie (2020)/Kept Movie (2020).strm", "http://ok/url");

        var result = await _service.CleanupAsync(
            _libraryRoot,
            expectedPaths: new HashSet<string> { kept },
            options: new CleanupOptions { Enabled = true, DryRun = false });

        Assert.True(result.Executed);
        Assert.Empty(result.OrphansFound);
        Assert.Empty(result.OrphansDeleted);
        Assert.True(File.Exists(kept));
    }

    // -------------------------------------------------------------------------

    private string CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_libraryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
            Directory.Delete(_libraryRoot, recursive: true);
    }
}
