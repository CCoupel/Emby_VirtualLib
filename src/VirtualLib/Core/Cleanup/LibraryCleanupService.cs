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

    /// <summary>
    /// Skips reparse points (symlinks/junctions) during enumeration — on Unix, plain
    /// SearchOption.AllDirectories follows symlinked directories, which would let a link placed
    /// inside the virtual library folder (accidental bind-mount, misconfiguration, or a local
    /// user with write access to the host) walk files outside the library's real footprint.
    /// See code-reviewer report on #44.
    /// </summary>
    private static readonly EnumerationOptions FileEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

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
        foreach (var path in Directory.EnumerateFiles(libraryFolderPath, "*", FileEnumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!normalizedExpected.Contains(NormalizePath(path)))
                orphans.Add(path);
        }

        var deleted = new List<string>();
        if (!options.DryRun)
        {
            // Defense in depth (belt-and-braces on top of the reparse-point skip above): never
            // delete a resolved path that falls outside the library folder, whatever the reason.
            var normalizedRoot = NormalizePath(libraryFolderPath) + Path.DirectorySeparatorChar;

            foreach (var orphan in orphans)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var normalizedOrphan = NormalizePath(orphan);
                if (!normalizedOrphan.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Cleanup: refusing to delete '{Path}' — resolved path escapes library folder '{Root}'",
                        orphan, libraryFolderPath);
                    continue;
                }

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
