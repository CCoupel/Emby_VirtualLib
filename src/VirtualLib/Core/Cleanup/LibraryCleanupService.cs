using Microsoft.Extensions.Logging;

namespace VirtualLib.Core.Cleanup;

/// <summary>
/// Implémentation filesystem de <see cref="ILibraryCleanupService"/>.
/// Garde-fous appliqués ici, en plus de ceux imposés à l'appelant (D11) :
/// - <see cref="CleanupOptions.Enabled"/> à <c>false</c> → abandon immédiat, aucun accès disque.
/// - <paramref name="expectedPaths"/> vide → abandon (filet de sécurité : un ensemble attendu
///   vide combiné à un dossier non-vide supprimerait tout, c'est presque toujours le signe
///   d'un appelant qui n'a pas dû invoquer ce service — cf. CA11 côté SyncService : abandon
///   inconditionnel si la synchronisation a échoué ou n'a retourné aucun item).
/// </summary>
public sealed class LibraryCleanupService : ILibraryCleanupService
{
    private readonly ILogger<LibraryCleanupService> _logger;

    public LibraryCleanupService(ILogger<LibraryCleanupService> logger)
    {
        _logger = logger;
    }

    public Task<CleanupResult> CleanupAsync(
        string libraryFolderPath,
        ISet<string> expectedPaths,
        CleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return Task.FromResult(CleanupResult.Aborted("Cleanup disabled (CleanupOptions.Enabled == false)"));

        if (expectedPaths.Count == 0)
            return Task.FromResult(CleanupResult.Aborted(
                "Empty expected-path set — refusing to avoid wiping the library (safety backstop)"));

        if (!Directory.Exists(libraryFolderPath))
            return Task.FromResult(new CleanupResult { Executed = true });

        var normalizedExpected = new HashSet<string>(
            expectedPaths.Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var orphans = new List<string>();
        foreach (var path in Directory.EnumerateFiles(libraryFolderPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!normalizedExpected.Contains(NormalizePath(path)))
                orphans.Add(path);
        }

        var deleted = new List<string>();
        if (!options.DryRun)
        {
            foreach (var orphan in orphans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(orphan);
                    deleted.Add(orphan);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cleanup: failed to delete orphan file '{Path}'", orphan);
                }
            }
        }

        _logger.LogInformation(
            "Cleanup '{LibraryFolderPath}': {Total} orphan(s) found, {Deleted} deleted (dryRun={DryRun})",
            libraryFolderPath, orphans.Count, deleted.Count, options.DryRun);

        if (orphans.Count > 0)
            _logger.LogDebug("Cleanup orphan paths: {Paths}", string.Join(", ", orphans));

        return Task.FromResult(new CleanupResult
        {
            Executed = true,
            OrphansFound = orphans,
            OrphansDeleted = deleted
        });
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
