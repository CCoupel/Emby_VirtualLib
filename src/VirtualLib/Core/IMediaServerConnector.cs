using VirtualLib.Core.Models;

namespace VirtualLib.Core;

/// <summary>
/// Abstraction d'un serveur média distant.
/// Toute implémentation doit être thread-safe et stateless
/// (la configuration est injectée au constructeur).
/// </summary>
public interface IMediaServerConnector : IDisposable
{
    /// <summary>Identifiant du type de serveur : "Emby", "Jellyfin", "Plex"</summary>
    string ServerType { get; }

    /// <summary>GUID unique de cette instance de connecteur (depuis ConnectorConfig.Id)</summary>
    string ConnectorId { get; }

    /// <summary>Nom d'affichage configuré par l'utilisateur</summary>
    string DisplayName { get; }

    /// <summary>Teste la connectivité et l'authentification</summary>
    Task<ConnectorTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Liste toutes les bibliothèques disponibles sur le serveur distant</summary>
    Task<IReadOnlyList<RemoteLibrary>> ListLibrariesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Liste tous les items d'une bibliothèque distante.
    /// Gère la pagination en interne — retourne la liste complète.
    /// </summary>
    Task<IReadOnlyList<MediaItem>> ListItemsAsync(
        string libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>Retourne les métadonnées complètes d'un item</summary>
    Task<MediaMetadata> GetMetadataAsync(
        string itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Construit l'URL de stream pour un item.
    /// Cette URL sera utilisée par le ProxyController pour pipe le flux.
    /// </summary>
    Task<string> GetStreamUrlAsync(
        string itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retourne le stream binaire d'un artwork.
    /// Retourne null si l'artwork n'existe pas.
    /// </summary>
    Task<Stream?> GetArtworkStreamAsync(
        string itemId,
        ArtworkType artworkType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retourne le nombre d'éléments dans une bibliothèque distante.
    /// Utilise Limit=0 pour ne récupérer que le TotalRecordCount, sans charger les items.
    /// </summary>
    Task<int> GetItemCountAsync(string libraryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Télécharge le fichier source d'un item (ebook, etc.) vers le chemin indiqué (sans extension).
    /// Détermine l'extension réelle depuis les en-têtes HTTP ou les métadonnées de la source,
    /// et retourne le chemin complet du fichier créé.
    /// </summary>
    Task<string> DownloadFileToPathAsync(string itemId, string destPathNoExt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifie le serveur distant qu'une lecture vient de démarrer.
    /// <paramref name="playSessionId"/> est un GUID unique par session de lecture,
    /// à réutiliser dans les appels Progress et Stopped correspondants.
    /// <paramref name="deviceName"/> identifie le client local sous la forme "user@app"
    /// (ex: "cyril@Emby Web") tel qu'il apparaîtra dans le tableau de bord du serveur distant.
    /// </summary>
    Task ReportPlaybackStartAsync(string itemId, string playSessionId, string deviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envoie la position de lecture courante au serveur distant (~toutes les 10s).
    /// </summary>
    Task ReportPlaybackProgressAsync(string itemId, string playSessionId, string deviceName, long positionTicks, bool isPaused, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifie le serveur distant que la lecture s'est arrêtée.
    /// <paramref name="positionTicks"/> est la position finale (en ticks) pour sauvegarder l'avancement.
    /// </summary>
    Task ReportPlaybackStoppedAsync(string itemId, string playSessionId, string deviceName, long positionTicks, CancellationToken cancellationToken = default);
}
