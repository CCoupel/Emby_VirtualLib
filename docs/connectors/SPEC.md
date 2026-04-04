# Spécification — IMediaServerConnector

## Objectif

`IMediaServerConnector` est le contrat d'abstraction central du plugin. Toute source de médias distante (Emby, Jellyfin, Plex...) doit implémenter cette interface pour être utilisable par le core.

---

## Interface

```csharp
public interface IMediaServerConnector : IDisposable
{
    /// <summary>Identifiant du type de serveur : "Emby", "Plex"</summary>
    string ServerType { get; }

    /// <summary>GUID unique de cette instance (depuis ConnectorConfig.Id)</summary>
    string ConnectorId { get; }

    /// <summary>Nom d'affichage configuré par l'utilisateur</summary>
    string DisplayName { get; }

    Task<ConnectorTestResult>          TestConnectionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteLibrary>> ListLibrariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MediaItem>>     ListItemsAsync(string libraryId, CancellationToken ct = default);
    Task<int>                          GetItemCountAsync(string libraryId, CancellationToken ct = default);
    Task<MediaMetadata>                GetMetadataAsync(string itemId, CancellationToken ct = default);
    Task<string>                       GetStreamUrlAsync(string itemId, CancellationToken ct = default);
    Task<Stream?>                      GetArtworkStreamAsync(string itemId, ArtworkType type, CancellationToken ct = default);
    Task<string>                       DownloadFileToPathAsync(string itemId, string destPathNoExt, CancellationToken ct);
    Task                               ReportPlaybackStartAsync(string itemId, CancellationToken ct = default);
    Task                               ReportPlaybackStoppedAsync(string itemId, CancellationToken ct = default);
}
```

---

## Modèles normalisés

### ConnectorTestResult

```csharp
public class ConnectorTestResult
{
    public bool    Success       { get; init; }
    public string? ErrorMessage  { get; init; }
    public string? ServerVersion { get; init; }

    public static ConnectorTestResult Ok(string version)   => new() { Success = true, ServerVersion = version };
    public static ConnectorTestResult Fail(string error)   => new() { Success = false, ErrorMessage = error };
}
```

### RemoteLibrary

```csharp
public class RemoteLibrary
{
    public string      Id   { get; init; }
    public string      Name { get; init; }
    public LibraryType Type { get; init; }
}

public enum LibraryType { Movies, TvShows, Music, AudioBooks, Books, Photos, Mixed, Unknown }
```

### MediaItem

```csharp
public class MediaItem
{
    public string   RemoteId { get; init; }
    public string   Title    { get; init; }
    public MediaType Type    { get; init; }
    public int?     Year     { get; init; }

    // Séries / livres audio
    public string? SeriesId     { get; init; }  // Show ID (épisodes) ou Album ID (chapitres)
    public string? SeasonId     { get; init; }  // Saison ID — requis pour artwork/NFO de saison
    public string? SeriesName   { get; init; }
    public int?    SeasonNumber  { get; init; }
    public int?    EpisodeNumber { get; init; }

    // Identifiants externes
    public string? ImdbId { get; init; }
    public string? TmdbId { get; init; }
    public string? TvdbId { get; init; }

    public DateTime? DateAdded { get; init; }

    /// <summary>Durée en ticks 100 ns. Plex : duration(ms) × 10_000. Emby : RunTimeTicks direct.</summary>
    public long? RuntimeTicks { get; init; }

    public IReadOnlyList<string>      AlbumArtists    { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ArtworkType> AvailableArtwork { get; init; } = Array.Empty<ArtworkType>();

    /// <summary>
    /// Infos techniques issues des MediaSources distants.
    /// Doit être peuplé par ListItemsAsync ET GetMetadataAsync.
    /// Utilisé pour écrire &lt;fileinfo&gt;&lt;streamdetails&gt; dans le NFO AVANT le scan Emby.
    /// </summary>
    public TechnicalInfo? Technical { get; init; }

    // ── États utilisateur (v1.6.0) ──────────────────────────────────────────
    // Propagés depuis le serveur source pour l'utilisateur authentifié.
    // Règle merge : seule l'augmentation est propagée (jamais de réduction).
    // Emby  : champ UserData dans GET /Users/{userId}/Items.
    // Plex  : attributs viewCount / viewOffset / lastViewedAt sur <Video>.

    /// <summary>true si l'item a été regardé en totalité sur le serveur source.</summary>
    public bool      IsPlayed              { get; init; }
    /// <summary>Nombre de lectures complètes sur le serveur source.</summary>
    public int       PlayCount             { get; init; }
    /// <summary>Date de la dernière lecture complète (UTC).</summary>
    public DateTime? LastPlayedDate        { get; init; }
    /// <summary>true si l'item est favori sur le serveur source.</summary>
    public bool      IsFavorite            { get; init; }
    /// <summary>Position de reprise en ticks 100 ns. Plex : viewOffset(ms) × 10_000. 0 = aucune.</summary>
    public long      PlaybackPositionTicks { get; init; }
}

public enum MediaType { Movie, Episode, Music, Photo, Book, AudioBook }
```

### TechnicalInfo

```csharp
/// <summary>
/// Infos de probing issues du serveur source.
/// C'est le SEUL mécanisme fiable pour injecter des MediaStream sur des .strm :
/// le bloc &lt;fileinfo&gt;&lt;streamdetails&gt; dans le NFO est lu par Emby au moment du scan.
/// UpdateItem(Width/Height) ne crée PAS de MediaStream.
/// </summary>
public sealed class TechnicalInfo
{
    public long?   Size            { get; init; }  // taille fichier en octets
    public int?    Bitrate         { get; init; }  // débit total en bps (Plex envoie kbps → ×1000)
    public string? Container       { get; init; }  // mkv, mp4, avi…
    public int?    Width           { get; init; }
    public int?    Height          { get; init; }
    public string? VideoCodec      { get; init; }
    public string? AudioCodec      { get; init; }
    public int?    AudioChannels   { get; init; }
    public int?    AudioSampleRate { get; init; }
}
```

### MediaMetadata

```csharp
/// <summary>Étend MediaItem avec les données éditoriales complètes.</summary>
public class MediaMetadata : MediaItem
{
    public string?                    Overview        { get; init; }
    public float?                     CommunityRating { get; init; }
    public int?                       RuntimeMinutes  { get; init; }
    public IReadOnlyList<string>      Genres          { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>      Studios         { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>      Tags            { get; init; } = Array.Empty<string>();
    public string?                    OfficialRating  { get; init; }
    public IReadOnlyList<PersonInfo>  Cast            { get; init; } = Array.Empty<PersonInfo>();
    public IReadOnlyList<string>      Directors       { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>      Writers         { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>      Authors         { get; init; } = Array.Empty<string>();
    public string?                    Tagline         { get; init; }
    public string?                    TrailerUrl      { get; init; }
    // Technical + états utilisateur hérités de MediaItem
}
```

### ArtworkType

```csharp
public enum ArtworkType
{
    Poster,    // poster.jpg (vidéo) ou folder.jpg (audio)
    Backdrop,  // fanart.jpg
    Thumb,     // landscape.jpg / thumb.jpg
    Logo,      // logo.png / clearlogo.png
    Banner,    // banner.jpg
    Disc,      // disc.jpg
    Art        // clearart.png
}
```

---

## Contrats comportementaux

### TestConnectionAsync
- Doit valider à la fois la connectivité réseau ET l'authentification
- Timeout recommandé : 10 secondes
- Ne doit jamais lever d'exception — retourner `ConnectorTestResult.Fail(message)` à la place

### ListLibrariesAsync
- Retourner uniquement les bibliothèques de type supporté
- Si aucune bibliothèque disponible : retourner liste vide (pas d'exception)

### ListItemsAsync
- Gérer la pagination en interne
- Propager le `CancellationToken` à chaque appel paginé
- Loguer un warning pour les items dont le type n'est pas reconnu, puis les ignorer
- En cas d'erreur partielle : loguer et retourner les items déjà collectés
- **Peupler les états utilisateur** (`IsPlayed`, `PlayCount`, `LastPlayedDate`, `IsFavorite`, `PlaybackPositionTicks`) depuis le serveur source pour l'utilisateur authentifié

### GetMetadataAsync
- Doit peupler `Technical` (TechnicalInfo) depuis les MediaSources
- Doit peupler les états utilisateur (même logique que `ListItemsAsync`)

### GetStreamUrlAsync
- L'URL retournée doit être directement utilisable dans `HttpClient.GetAsync()`
- Elle peut inclure un token dans le query string
- Elle doit supporter les `Range` headers HTTP

### GetArtworkStreamAsync
- Retourner `null` si l'artwork n'existe pas (ne pas lever d'exception)
- Le stream retourné est la responsabilité de l'appelant (dispose obligatoire)

---

## ConnectorFactory

```csharp
public class ConnectorFactory : IConnectorFactory
{
    public IMediaServerConnector Create(ConnectorConfig config) => config.ServerType switch
    {
        "Emby"     => new EmbyConnector(config, _httpClientFactory, _loggerFactory.CreateLogger<EmbyConnector>()),
        "Plex"     => new PlexConnector(config, _httpClientFactory, _loggerFactory.CreateLogger<PlexConnector>()),
        _ => throw new NotSupportedException($"Server type '{config.ServerType}' is not supported")
    };
}
```
