# Architecture — VirtualLib Plugin

## Vue d'ensemble

VirtualLib est un plugin Emby qui expose les bibliothèques de serveurs médias distants comme des bibliothèques natives sur le serveur hôte. Il repose sur trois mécanismes :

1. **Sync** — interroge périodiquement les serveurs sources pour construire une arborescence de fichiers `.strm` + `.nfo`
2. **Index** — Emby indexe cette arborescence comme une bibliothèque normale
3. **Proxy** — un endpoint HTTP intégré au plugin proxifie le stream depuis la source, permettant le transcodage par le serveur hôte

---

## Composants détaillés

### 1. IMediaServerConnector

Interface centrale d'abstraction. Chaque type de serveur source implémente cette interface.

```csharp
public interface IMediaServerConnector : IDisposable
{
    string ServerType { get; }
    string ConnectorId { get; }
    string DisplayName { get; }

    Task<ConnectorTestResult> TestConnectionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteLibrary>> ListLibrariesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MediaItem>> ListItemsAsync(string libraryId, CancellationToken ct = default);
    Task<int> GetItemCountAsync(string libraryId, CancellationToken ct = default);
    Task<MediaMetadata> GetMetadataAsync(string itemId, CancellationToken ct = default);
    Task<string> GetStreamUrlAsync(string itemId, CancellationToken ct = default);
    Task<Stream?> GetArtworkStreamAsync(string itemId, ArtworkType type, CancellationToken ct = default);
    Task<string> DownloadFileToPathAsync(string itemId, string destPathNoExt, CancellationToken ct);
    Task ReportPlaybackStartAsync(string itemId, CancellationToken ct = default);
    Task ReportPlaybackStoppedAsync(string itemId, CancellationToken ct = default);
}
```

**Modèles normalisés** (indépendants du serveur source) :

```csharp
public class MediaItem
{
    public string RemoteId { get; init; }
    public string Title { get; init; }
    public MediaType Type { get; init; }
    public int? Year { get; init; }
    public string? SeriesId { get; init; }       // séries : show ID ; audiobooks : album ID
    public string? SeasonId { get; init; }       // séries : saison ID
    public string? SeriesName { get; init; }     // audiobooks : titre du livre
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string? ImdbId { get; init; }
    public string? TmdbId { get; init; }
    public string? TvdbId { get; init; }
    public DateTime? DateAdded { get; init; }
    /// <summary>Durée en ticks 100 ns (même unité que Emby RunTimeTicks).</summary>
    public long? RuntimeTicks { get; init; }
    public IReadOnlyList<string> AlbumArtists { get; init; }  // auteurs livres audio
    public IReadOnlyList<ArtworkType> AvailableArtwork { get; init; }
    /// <summary>Infos techniques issues des MediaSources distants.</summary>
    public TechnicalInfo? Technical { get; init; }
}

/// <summary>
/// Infos techniques probing du serveur source (déjà analysées par la source).
/// Injectées dans le NFO via &lt;fileinfo&gt;&lt;streamdetails&gt; AVANT le scan Emby.
/// C'est le seul mécanisme fiable pour créer des MediaStream sur des fichiers .strm.
/// (UpdateItem avec Width/Height ne crée PAS de MediaStream — seulement le NFO le fait.)
/// </summary>
public sealed class TechnicalInfo
{
    public long?   Size            { get; init; }  // taille en octets
    public int?    Bitrate         { get; init; }  // débit total en bps
    public string? Container       { get; init; }  // mkv, mp4, avi…
    public int?    Width           { get; init; }  // largeur vidéo en pixels
    public int?    Height          { get; init; }  // hauteur vidéo en pixels
    public string? VideoCodec      { get; init; }  // h264, hevc…
    public string? AudioCodec      { get; init; }  // ac3, aac…
    public int?    AudioChannels   { get; init; }
    public int?    AudioSampleRate { get; init; }
}

public class MediaMetadata : MediaItem
{
    public string? Overview { get; init; }
    public float? CommunityRating { get; init; }
    public int? RuntimeMinutes { get; init; }
    public IReadOnlyList<string> Genres { get; init; }
    public IReadOnlyList<string> Studios { get; init; }
    public IReadOnlyList<string> Tags { get; init; }
    public string? OfficialRating { get; init; }
    public IReadOnlyList<PersonInfo> Cast { get; init; }
    public IReadOnlyList<string> Directors { get; init; }
    public IReadOnlyList<string> Writers { get; init; }
    public IReadOnlyList<string> Authors { get; init; }   // livres / livres audio
    public string? Tagline { get; init; }
    public string? TrailerUrl { get; init; }
    // Technical est hérité de MediaItem — doit être peuplé par GetMetadataAsync
}

public enum MediaType { Movie, Episode, Music, Photo, Book, AudioBook }

public enum ArtworkType
{
    Poster,    // Primary / cover     → poster.jpg / folder.jpg
    Backdrop,  // Fanart / background → fanart.jpg
    Thumb,     // Landscape thumbnail → landscape.jpg
    Logo,      // ClearLogo           → logo.png
    Banner,    // Wide banner         → banner.jpg
    Disc,      // Disc / CD art       → disc.jpg
    Art        // ClearArt            → clearart.png
}
```

---

### 2. EmbyConnector

Implémentation de `IMediaServerConnector` pour les serveurs Emby (et Jellyfin dont l'API est compatible).

**Authentification** :
- API Key dans le header `X-Emby-Token`
- Ou `X-MediaBrowser-Token` (compatibilité Jellyfin)

**Pattern d'URL — règle importante** :
`ConnectorConfig.ServerUrl` doit inclure le chemin de base complet (ex : `https://media.example.com/emby`).
`HttpClient.BaseAddress` est défini sur `{ServerUrl}/` (avec slash final).
Les chemins relatifs dans le connector ne doivent PAS répéter ce préfixe :

```csharp
// ✅ Correct — BaseAddress = "https://media.example.com/emby/"
"Library/VirtualFolders"
$"Users/{userId}/Items?..."
$"Items/{itemId}?..."

// ❌ Incorrect — double préfixe /emby/emby/
"emby/Library/VirtualFolders"
```

**Endpoints utilisés** :
```
GET Library/VirtualFolders              → liste des bibliothèques
GET Users/{userId}/Items?ParentId={id}  → items d'une bibliothèque
GET Items/{itemId}                      → métadonnées d'un item
GET Items/{itemId}/Images/{type}        → artwork
GET {ServerUrl}/Videos/{itemId}/stream  → stream vidéo (URL absolue)
```

**Gestion de la pagination** :
```csharp
// L'API Emby pagine avec StartIndex + Limit
// Le connector itère jusqu'à récupérer tous les items
while (startIndex < totalCount)
{
    var page = await GetItemsPageAsync(libraryId, startIndex, pageSize, ct);
    items.AddRange(page.Items);
    startIndex += pageSize;
    totalCount = page.TotalRecordCount;
}
```

---

### 3. StrmGenerator

Génère les fichiers `.strm` dans l'arborescence de la bibliothèque virtuelle.

**URL dans le .strm** — pointe vers le ProxyController sur le serveur hôte :
```
https://media.example.com/virtuallib/proxy/{connectorId}/{libraryId}/{remoteItemId}
```

Le `libraryId` est inclus pour permettre la validation côté proxy (bibliothèque active + droits utilisateur).
L'URL est configurée via `ProxyBaseUrl` dans les paramètres du plugin.

**Arborescence générée** :

Pour les films :
```
{virtualLibRoot}/
└── {LibraryName}/
    └── {Title} ({Year})/
        └── {Title} ({Year}).strm
```

Pour les séries :
```
{virtualLibRoot}/
└── {LibraryName}/
    └── {SeriesName}/
        └── Season {XX}/
            └── {SeriesName} - S{XX}E{YY}.strm
```

---

### 4. NfoGenerator

Génère les fichiers `.nfo` au format Kodi/Emby pour éviter le re-scraping.

**Méthodes disponibles** :
| Méthode | Fichier généré | Élément racine XML |
|---|---|---|
| `GenerateMovieNfo` | `{Title} ({Year}).nfo` | `<movie>` |
| `GenerateEpisodeNfo` | `{Series} - S{xx}E{yy}.nfo` | `<episodedetails>` |
| `GenerateShowNfo` | `tvshow.nfo` | `<tvshow>` |
| `GenerateSeasonNfo` | `season.nfo` | `<season>` |
| `GenerateAudioBookNfo` | `album.nfo` | `<album>` |
| `GenerateBookNfo` | `{Title} ({Year}).nfo` | `<book>` |
| `PatchStreamDetails` | modifie un NFO existant | — injecte `<fileinfo>` |

**movie.nfo** :
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Inception</title>
  <year>2010</year>
  <plot>Un voleur...</plot>
  <rating>8.8</rating>
  <runtime>148</runtime>
  <genre>Science-Fiction</genre>
  <studio>Warner Bros.</studio>
  <uniqueid type="imdb" default="true">tt1375666</uniqueid>
  <uniqueid type="tmdb">27205</uniqueid>
  <fileinfo>
    <streamdetails>
      <video>
        <codec>h264</codec>
        <width>1920</width>
        <height>1080</height>
        <durationinseconds>8880</durationinseconds>
      </video>
      <audio>
        <codec>aac</codec>
        <channels>2</channels>
      </audio>
    </streamdetails>
  </fileinfo>
</movie>
```

**Contrainte critique — `<fileinfo>` et fichiers `.strm`** :

Emby ne lance pas ffprobe sur les `.strm` (fichiers HTTP). Sans `<fileinfo>` dans le NFO,
aucun `MediaStream` n'est créé → pas de résolution, pas de codec, pas de durée affichée.

`UpdateItem(video.Width = 1920)` **ne crée pas de MediaStream** — il ne modifie que
l'objet item. Seul le scan du NFO avec `<fileinfo>` crée des entrées `MediaStream` en DB.

**Stratégie en deux temps** :
1. **Phase 1 (avant scan)** : le NFO est généré avec `<fileinfo>` d'emblée pour les nouveaux items.
   Pour les items existants (skip), `PatchStreamDetails` injecte `<fileinfo>` si absent.
2. **Phase 2 (après scan)** : `UpdateItem` injecte `RunTimeTicks`, `Size`, `TotalBitrate`.
   `PatchStreamDetails` est rappelé en cas de NFO non encore patché.

`PatchStreamDetails` est idempotent (no-op si `<fileinfo>` déjà présent ou données nulles).

**tvshow.nfo + season.nfo + episodedetails.nfo** pour les séries.
Artwork : `poster.jpg`, `fanart.jpg` au niveau show et season (téléchargés une fois par série/saison).

### 4b. PlexConnector — spécificités et contraintes

**Authentification** :
- API Key → header `X-Plex-Token` dès la construction
- UserCredentials → POST `https://plex.tv/users/sign_in.xml` → `authToken`; retry 401

**Format des réponses** : XML (pas JSON). Éléments racine variés :
| Type d'item | Élément XML | Attribut durée |
|---|---|---|
| Film / Episode | `<Video>` | `duration` en **ms** → `× 10 000` → ticks |
| Show / Saison | `<Directory>` | n/a (conteneurs) |
| Piste audio | `<Track>` | `duration` en **ms** |

**⚠ Pièges Plex** :
- `GetMetadataAsync` pour un Show ou Season retourne un élément `<Directory>`, pas `<Video>`.
  `MapDirectoryToMetadata` gère ce cas. Si seul `<Video>` est cherché → exception silencieuse.
- `duration` est en **millisecondes** ; Emby attend des ticks (100 ns). Conversion : `ms × 10_000`.
- `parentRatingKey` sur un épisode = `SeasonId` (la saison). Absent → le bloc artwork/NFO de saison ne s'exécute jamais.
- `TechnicalInfo` est parsé depuis l'élément `<Media>` enfant du `<Video>` :
  `videoCodec`, `audioCodec`, `width`, `height`, `bitrate` (en **kbps** → `× 1000` pour bps),
  `audioChannels`, `container`. Le bitrate total est en kbps côté Plex.

---

### 5. ProxyController

Endpoint HTTP enregistré dans le pipeline ASP.NET d'Emby.

```
GET /virtuallib/proxy/{connectorId}/{libraryId}/{itemId}
```

**Le DTO doit être marqué `[Unauthenticated]`** : ffprobe probe les STRM sans token d'authentification. Sans cet attribut, Emby retourne 401 et la lecture échoue immédiatement. Le contrôle d'accès est assuré par le plugin lui-même (voir ci-dessous).

**Comportement** :
1. Valide que le connector existe et est actif
2. Valide que `libraryId` est configurée sur le connector
3. Si token présent → vérifie que l'utilisateur a accès à la bibliothèque virtuelle Emby (fail-closed)
4. Si pas de token → autorisé uniquement si IP privée (RFC 1918) **et** User-Agent interne (`Lavf/*` = ffprobe, absent = Emby .NET client)
5. Demande l'URL de stream à `connector.GetStreamUrlAsync(itemId)`
6. Copie les headers pertinents (Content-Type, Content-Length, Accept-Ranges, Content-Range)
7. Supporte les `Range` headers pour permettre le seek
8. Pipe le body vers la réponse client via `Stream.CopyToAsync`

**Modèle de sécurité** :

| Requête | Résultat |
|---|---|
| Token valide + accès bibliothèque | ✅ autorisé |
| Token valide + pas d'accès | ❌ 401 |
| Sans token + IP privée + UA `Lavf/*` | ✅ autorisé (ffprobe) |
| Sans token + IP privée + pas de UA | ✅ autorisé (Emby interne) |
| Sans token + IP privée + UA navigateur | ❌ 401 |
| Sans token + IP publique | ❌ 401 |

**Note DI** : `ILogger<T>` n'est pas enregistré dans SimpleInjector pour les controllers Emby. Le controller utilise `NullLogger<ProxyController>.Instance`.

**Support Range (seek)** :
```csharp
// Forwarde le header Range de la requête client vers la source
if (!string.IsNullOrEmpty(range))
    remoteRequest.Headers.TryAddWithoutValidation("Range", range);
// Retourne 206 Partial Content si la source répond 206
```

**Point critique — Content-Range** :
En .NET `HttpClient`, `Content-Range` est dans `response.Content.Headers` (HttpContentHeaders),
**pas** dans `response.Headers` (HttpResponseHeaders). Ne pas le forwader cause des erreurs graves :
- ffprobe lit un chunk de N bytes (ex: les 1663 derniers octets d'un MKV)
- Voit Content-Length=1663 sans Content-Range → croit que le fichier fait 1663 bytes
- Emby stocke `Size=1663` dans MediaSource
- ffmpeg demande 1663 bytes, les obtient, voit EOF → "File ended prematurely"

```csharp
// ✅ Correct
var contentRangeHeader = remoteResponse.Content.Headers.ContentRange;
if (contentRangeHeader != null)
    forwardHeaders["Content-Range"] = contentRangeHeader.ToString();

// ❌ Incorrect — Content-Range n'est jamais dans response.Headers
var cr = remoteResponse.Headers.GetValues("Content-Range");
```

**Gestion des déconnexions client** :
ffprobe ferme la connexion après avoir lu quelques Mo (suffisant pour analyser les métadonnées).
`PipeWriterStream.DisposeAsync()` lève une exception si Content-Length était 22 Go mais seulement
quelques Mo ont été écrits. Il faut attraper explicitement :

```csharp
try { await remoteStream.CopyToAsync(outputStream, ct); }
catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
{
    // Client closed connection — not Emby's cancellation token
}
catch (IOException ex) when (!ct.IsCancellationRequested)
{
    // Broken pipe — normal for ffprobe
}
finally { try { await outputStream.DisposeAsync(); } catch { } }
```

---

### 6. LibrarySyncJob / SyncService

Tâche planifiée Emby (`IScheduledTask`). Délègue à `SyncService` qui orchestre la sync.

**Algorithme de sync en deux phases** :
```
Phase 1 — SyncService.SyncConnectorAsync()
  Pour chaque ConnectorConfig actif :
    1. TestConnection() — abandon si KO
    2. ListLibraries() — merge auto-découverte dans KnownLibraries
    3. Pour chaque bibliothèque configurée :
       a. ListItems() — tous les items (avec TechnicalInfo depuis MediaSources)
       b. Pour chaque item :
          - Génère toujours le .strm (idempotent, change la date de modif → déclenche rescan)
          - Si NFO absent ou RemoteSyncFull :
              · GetMetadataAsync() → MediaMetadata (avec Technical)
              · Génère .nfo avec <fileinfo><streamdetails>
              · Télécharge artwork (poster.jpg, fanart.jpg…)
          - Si NFO présent (RemoteSync skip) :
              · PatchStreamDetails() si <fileinfo> absent
          - Pour épisodes : GetMetadataAsync(SeriesId) → tvshow.nfo + artwork show (1x/série)
                            GetMetadataAsync(SeasonId) → season.nfo + artwork saison (1x/saison)
          - Ajoute à pendingStrms[]
    4. Scan ciblé de la bibliothèque (QueueLibraryScan)

Phase 2 — SyncService.PushMetadataAsync()
  Boucle de polling (toutes les 2 s, timeout 5 min) :
  Pour chaque item dans pendingStrms :
    · FindByPath(strmPath) → item en DB ?
    · Si oui → injecte RunTimeTicks, Size, TotalBitrate, Container, Width, Height via UpdateItem()
              → PatchStreamDetails() (idempotent — complémente la phase 1)
    · Si non → réessaie au prochain tour
```

**Pourquoi deux phases ?**
Emby ne lance pas ffprobe sur les `.strm`. Sans injection :
- Durée = inconnue → impossible de scrobbler / afficher la progression
- Pas de MediaStream → pas de résolution/codec affichés

Phase 1 (NFO `<fileinfo>`) crée les `MediaStream` lors du scan.
Phase 2 (`UpdateItem`) complète `RunTimeTicks` et `Size` directement sur l'item.

**Pourquoi le .strm est toujours régénéré ?**
Sa date de modification change → Emby détecte le changement et lance un rescan partiel,
ce qui permet au NFO patché d'être relu et les nouveaux `<fileinfo>` d'être appliqués.

### 7. Fournisseurs de métadonnées locaux (ILocalMetadataProvider)

Emby ne lit pas les `.nfo` Kodi pour les types `Audio` (chapitres) et `Folder` dans une
bibliothèque Audiobooks, ni pour `Book`. Trois providers custom comblent ces lacunes :

| Provider | Type Emby | Fichier lu | Rôle |
|---|---|---|---|
| `AudioBookNfoProvider` | `Audio` | `album.nfo` (dossier parent) | Injecte `Album`, `AlbumArtists`, `ProductionYear` sur chaque chapitre |
| `AudioBookFolderNfoProvider` | `Folder` | `album.nfo` (dans le dossier) | Injecte titre, synopsis, genres, auteurs sur le container du livre |
| `BookNfoProvider` | `Book` | `{filename}.nfo` | Injecte les métadonnées complètes pour les ebooks |

Ces providers sont auto-découverts par Emby via le plugin assembly (pas de registration manuelle).

**⚠ Contrainte DI SimpleInjector** :
Emby utilise SimpleInjector qui **n'enregistre pas** `ILogger<T>` générique.
Les providers ne doivent pas injecter `ILogger<T>` dans leur constructeur — cela provoque
une `ActivationException` au chargement du plugin. Utiliser `NullLogger<T>.Instance` ou
ne pas logger.

---

### 8. PluginConfiguration

Configuration stockée par Emby dans son répertoire de données.

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    public List<ConnectorConfig> Connectors { get; set; } = new();
    public string VirtualLibraryRootPath { get; set; } = "";
    public int SyncIntervalHours { get; set; } = 6;
    public int ProxyTimeoutSeconds { get; set; } = 30;
    // Override de l'URL de base pour les .strm (utile derrière un reverse proxy)
    // Exemple : "https://media.example.com/emby2"
    // Si vide, l'URL est auto-détectée depuis les headers X-Forwarded-*
    public string ProxyBaseUrl { get; set; } = string.Empty;
}

public class ConnectorConfig
{
    public string Id { get; set; }              // GUID unique
    public string DisplayName { get; set; }     // Nom affiché dans l'UI
    public string ServerType { get; set; }      // "Emby" | "Jellyfin" | "Plex"
    // URL complète incluant le chemin de base Emby
    // Exemple : "https://media.example.com/emby" (sans slash final)
    public string ServerUrl { get; set; }
    public string ApiKey { get; set; }
    public List<string> LibraryIds { get; set; } = new();  // bibliothèques à sync
    public bool Enabled { get; set; } = true;
}
```

---

## Flux de données complet

### Lors de la sync
```
LibrarySyncJob
  → EmbyConnector.ListItemsAsync()
      → GET http://B:8096/emby/Users/{id}/Items
      ← JSON items[]
  → StrmGenerator.Generate(item)
      → écrit /virtual/Films_B/Inception (2010)/Inception (2010).strm
         contenu: https://media.example.com/virtuallib/proxy/emby-b/lib456/12345
  → NfoGenerator.Generate(metadata)
      → écrit /virtual/Films_B/Inception (2010)/Inception (2010).nfo
  → ArtworkDownloader.Download(item)
      → GET http://B:8096/emby/Items/12345/Images/Primary
      → écrit /virtual/Films_B/Inception (2010)/poster.jpg
  → ILibraryManager.ValidateMediaLibrary()  (trigger rescan)
```

### Lors de la lecture
```
Client Emby
  → browse /virtual/Films_B → Emby A sert depuis son index local
  → play Inception
      → GET https://media.example.com/virtuallib/proxy/emby-b/lib456/12345
          → ProxyController
              → EmbyConnector.GetStreamUrlAsync("12345")
                  ← "http://B:8096/Videos/12345/stream?api_key=TOKEN"
              → HttpClient.GetAsync(streamUrl, Range: bytes=0-)
              → pipe Response.Body ← upstream.Content.ReadAsStreamAsync()
      ← stream (Emby A peut transcoder avant d'envoyer au client)
```

---

## Considérations de sécurité

- Les API keys des serveurs sources sont stockées dans la config Emby (chiffrée sur disque)
- Les tokens ne sont jamais exposés dans les URLs .strm (tout passe par le proxy)
- Le ProxyController valide connector + libraryId avant de proxifier
- Accès navigateur sans token bloqué par filtre User-Agent
- Accès utilisateur avec token → vérification droits sur la bibliothèque virtuelle Emby
- Le vrai point de contrôle pour les requêtes internes (ffprobe) est Emby lui-même via `PlaybackInfo` — Emby ne transmet pas le token utilisateur aux appels ffprobe sur les URL .strm

---

## Extensibilité future

Pour ajouter un nouveau type de serveur :
1. Créer `PlexConnector : IMediaServerConnector`
2. Implémenter la traduction API Plex XML → modèles normalisés
3. Enregistrer dans le conteneur DI du plugin
4. Ajouter `"Plex"` dans la liste des `ServerType` disponibles dans l'UI

Le core (StrmGenerator, NfoGenerator, ProxyController, LibrarySyncJob) ne change pas.
