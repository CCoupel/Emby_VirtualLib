namespace VirtualLib.Core.Cleanup;

/// <summary>
/// Options d'exécution d'un nettoyage — brique partagée entre #12 (suppression distante) et
/// #44 (item écarté par une règle de filtre) : une seule cause technique de disparition d'un
/// fichier attendu, un seul mécanisme (D11).
/// </summary>
public sealed class CleanupOptions
{
    /// <summary>
    /// Interrupteur global. Défaut <c>false</c> — le nettoyage n'est jamais actif tant que
    /// l'utilisateur ne l'a pas explicitement activé.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Défaut <c>true</c> : journalise ce qui serait supprimé sans rien supprimer. Doit être
    /// explicitement désactivé (UI de configuration fine — #12) pour que des fichiers soient
    /// réellement effacés.
    /// </summary>
    public bool DryRun { get; init; } = true;
}

/// <summary>Résultat d'un appel à <see cref="ILibraryCleanupService.CleanupAsync"/>.</summary>
public sealed class CleanupResult
{
    /// <summary>
    /// <c>false</c> si le nettoyage a été abandonné par un garde-fou (désactivé, ensemble de
    /// chemins attendus vide, dossier absent) — dans ce cas <see cref="AbortReason"/> explique
    /// pourquoi et aucune analyse du disque n'a été effectuée.
    /// </summary>
    public bool Executed { get; init; }

    public string? AbortReason { get; init; }

    /// <summary>Fichiers présents sur disque et absents de l'ensemble attendu.</summary>
    public IReadOnlyList<string> OrphansFound { get; init; } = Array.Empty<string>();

    /// <summary>Sous-ensemble de <see cref="OrphansFound"/> réellement supprimé. Toujours vide en DryRun.</summary>
    public IReadOnlyList<string> OrphansDeleted { get; init; } = Array.Empty<string>();

    public static CleanupResult Aborted(string reason) => new() { Executed = false, AbortReason = reason };
}

/// <summary>
/// Détecte et (optionnellement) supprime les fichiers orphelins d'une bibliothèque virtuelle :
/// tout fichier présent sur disque et absent de l'ensemble des chemins attendus après une
/// synchronisation — que la cause soit une suppression côté serveur distant (#12) ou un item
/// désormais écarté par une règle de filtre (#44).
/// </summary>
public interface ILibraryCleanupService
{
    /// <summary>
    /// Compare le contenu réel de <paramref name="libraryFolderPath"/> à
    /// <paramref name="expectedPaths"/> et supprime les orphelins si <paramref name="options"/>
    /// l'autorise. Ne lève jamais pour un échec de suppression individuel (journalisé et
    /// ignoré) ; peut lever pour une erreur d'énumération du disque.
    /// </summary>
    Task<CleanupResult> CleanupAsync(
        string libraryFolderPath,
        ISet<string> expectedPaths,
        CleanupOptions options,
        CancellationToken cancellationToken = default);
}
