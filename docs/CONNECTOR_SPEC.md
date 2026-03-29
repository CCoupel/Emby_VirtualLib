# Spécification — IMediaServerConnector

## Objectif

`IMediaServerConnector` est le contrat d'abstraction central du plugin. Toute source de médias distante (Emby, Jellyfin, Plex, Subsonic...) doit implémenter cette interface pour être utilisable par le core du plugin.

---

## Interface complète

```csharp
namespace VirtualLib.Core
{
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
    }
}
```

---

## Modèles

### ConnectorTestResult

```csharp
public class ConnectorTestResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ServerVersion { get; init; }

    public static ConnectorTestResult Ok(string version) =>
        new() { Success = true, ServerVersion = version };

    public static ConnectorTestResult Fail(string error) =>
        new() { Success = false, ErrorMessage = error };
}
```

### RemoteLibrary

```csharp
public class RemoteLibrary
{
    public string Id { get; init; }
    public string Name { get; init; }
    public LibraryType Type { get; init; }
}

public enum LibraryType
{
    Movies,
    TvShows,
    Music,
    Mixed,
    Unknown
}
```

### MediaItem

```csharp
public class MediaItem
{
    public string RemoteId { get; init; }
    public string Title { get; init; }
    public MediaType Type { get; init; }
    public int? Year { get; init; }

    // Séries uniquement
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }

    // Identifiants externes
    public string? ImdbId { get; init; }
    public string? TmdbId { get; init; }
    public string? TvdbId { get; init; }

    public DateTime? DateAdded { get; init; }

    // Artworks disponibles (types présents sur le serveur distant)
    public IReadOnlyList<ArtworkType> AvailableArtwork { get; init; } = Array.Empty<ArtworkType>();
}

public enum MediaType
{
    Movie,
    Episode,
    Music,
    Photo
}
```

### MediaMetadata

```csharp
/// <summary>Étend MediaItem avec les données éditoriales complètes</summary>
public class MediaMetadata : MediaItem
{
    public string? Overview { get; init; }
    public float? CommunityRating { get; init; }
    public int? RuntimeMinutes { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Studios { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? OfficialRating { get; init; }   // "PG-13", "R", etc.
}
```

### ArtworkType

```csharp
public enum ArtworkType
{
    Poster,     // Affiche verticale (poster.jpg)
    Backdrop,   // Image de fond (fanart.jpg)
    Thumb,      // Miniature épisode (thumb.jpg)
    Logo        // Logo transparent (clearlogo.png)
}
```

---

## Contrats comportementaux

### TestConnectionAsync
- Doit valider à la fois la connectivité réseau ET l'authentification
- Timeout recommandé : 10 secondes
- Ne doit jamais lever d'exception — retourner `ConnectorTestResult.Fail(message)` à la place

### ListLibrariesAsync
- Retourner uniquement les bibliothèques de type supporté (Movies, TvShows, Music)
- Si aucune bibliothèque disponible : retourner liste vide (pas d'exception)

### ListItemsAsync
- Gérer la pagination en interne
- Propager le `CancellationToken` à chaque appel paginé
- Loguer un warning pour les items dont le type n'est pas reconnu, puis les ignorer
- En cas d'erreur partielle (une page échoue) : loguer et retourner les items déjà collectés

### GetStreamUrlAsync
- L'URL retournée doit être directement utilisable dans un `HttpClient.GetAsync()`
- Elle peut inclure un token d'authentification dans le query string
- Elle doit supporter les `Range` headers HTTP pour permettre le seek

### GetArtworkStreamAsync
- Retourner `null` si l'artwork n'existe pas (ne pas lever d'exception)
- Le stream retourné est la responsabilité de l'appelant (dispose obligatoire)

---

## Implémentation de référence : EmbyConnector

```csharp
public class EmbyConnector : IMediaServerConnector
{
    private readonly ConnectorConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmbyConnector> _logger;

    public string ServerType => "Emby";
    public string ConnectorId => _config.Id;
    public string DisplayName => _config.DisplayName;

    public EmbyConnector(
        ConnectorConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger<EmbyConnector> logger)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri(config.ServerUrl);
        _httpClient.DefaultRequestHeaders.Add("X-Emby-Token", config.ApiKey);
        _logger = logger;
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/emby/System/Info/Public", ct);
            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<EmbySystemInfo>(ct);
            return ConnectorTestResult.Ok(info?.Version ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test failed for {ConnectorId}", ConnectorId);
            return ConnectorTestResult.Fail(ex.Message);
        }
    }

    // ... autres méthodes
}
```

---

## Enregistrement DI

Le plugin enregistre les connecteurs dans son conteneur de services :

```csharp
// Dans Plugin.cs ou Startup
services.AddSingleton<IConnectorFactory, ConnectorFactory>();

// ConnectorFactory résout le bon IMediaServerConnector selon ConnectorConfig.ServerType
public class ConnectorFactory : IConnectorFactory
{
    public IMediaServerConnector Create(ConnectorConfig config) => config.ServerType switch
    {
        "Emby"     => new EmbyConnector(config, _httpClientFactory, _loggerFactory.CreateLogger<EmbyConnector>()),
        "Jellyfin" => new JellyfinConnector(config, _httpClientFactory, _loggerFactory.CreateLogger<JellyfinConnector>()),
        "Plex"     => new PlexConnector(config, _httpClientFactory, _loggerFactory.CreateLogger<PlexConnector>()),
        _ => throw new NotSupportedException($"Server type '{config.ServerType}' is not supported")
    };
}
```
