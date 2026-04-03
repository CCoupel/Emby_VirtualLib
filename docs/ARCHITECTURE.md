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
public interface IMediaServerConnector
{
    string ServerType { get; }
    string ServerId { get; }

    Task<ConnectorTestResult> TestConnectionAsync(CancellationToken ct);
    Task<IEnumerable<RemoteLibrary>> ListLibrariesAsync(CancellationToken ct);
    Task<IEnumerable<MediaItem>> ListItemsAsync(string libraryId, CancellationToken ct);
    Task<MediaMetadata> GetMetadataAsync(string itemId, CancellationToken ct);
    Task<string> GetStreamUrlAsync(string itemId, CancellationToken ct);
    Task<Stream> GetArtworkStreamAsync(string itemId, ArtworkType type, CancellationToken ct);
}
```

**Modèles normalisés** (indépendants du serveur source) :

```csharp
public class MediaItem
{
    public string RemoteId { get; set; }               // ID natif sur le serveur source
    public string Title { get; set; }
    public MediaType Type { get; set; }
    public int? Year { get; set; }
    public string? SeriesId { get; set; }              // AudioBook : AlbumId (container)
    public string? SeriesName { get; set; }            // AudioBook : Album (titre du livre)
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? ImdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? TvdbId { get; set; }
    public DateTime? DateAdded { get; set; }
    public long? RuntimeTicks { get; set; }            // Durée en ticks 100 ns (= Emby RunTimeTicks)
    public IReadOnlyList<string> AlbumArtists { get; set; } // Auteurs (livres audio)
    public IReadOnlyList<ArtworkType> AvailableArtwork { get; set; }
}

public class MediaMetadata : MediaItem
{
    public string? Overview { get; set; }
    public float? Rating { get; set; }
    public IEnumerable<string> Genres { get; set; }
    public IEnumerable<string> Studios { get; set; }
    public int? RuntimeMinutes { get; set; }
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
  <uniqueid type="imdb">tt1375666</uniqueid>
  <uniqueid type="tmdb">27205</uniqueid>
</movie>
```

**tvshow.nfo + episodedetails.nfo** pour les séries.

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

### 6. LibrarySyncJob

Tâche planifiée Emby (`IScheduledTask`) qui orchestre la synchronisation.

**Algorithme de sync** :
```
Pour chaque ConnectorConfig actif :
  Phase 1 — Génération des fichiers
    1. TestConnection() — skip si KO
    2. ListLibraries() — récupère les bibliothèques configurées
    3. Pour chaque bibliothèque :
       a. ListItems() — liste tous les items distants
       b. Génère .strm + .nfo + télécharge artwork (poster, fanart, banner, disc, clearart…)
       c. Pour les livres audio : album.nfo + folder.jpg dans le dossier du livre
          → collecte les chapitres dans pendingChapters[]
  Phase 2 — Injection de métadonnées post-scan (livres audio seulement)
    4. QueueLibraryScan() — déclenche le scan Emby des nouveaux fichiers
    5. Boucle de polling (background, toutes les 2 s, timeout 5 min) :
       - Pour chaque chapitre dans pendingChapters :
         · ILibraryManager.FindByPath(strmPath) → Audio item en DB ?
         · Si oui → injecte RunTimeTicks, Album, AlbumArtists via UpdateItem()
                  → retire de la liste
         · Si non → réessaie au prochain tour
       - S'arrête quand la liste est vide ou timeout atteint
```

**Pourquoi le polling ?**
Emby ne fait pas de ffprobe sur les fichiers `.strm` (protocole HTTP, non local). La durée
et les champs de groupement (`Album`, `AlbumArtists`) ne sont donc jamais remplis
automatiquement. Le polling injecte ces données directement en DB dès que le scan crée
les items, sans attendre une seconde synchronisation.

**Détection de delta** (backlog) :
- Fichier index JSON local : `{virtualLibRoot}/.index/{connectorId}.json`
- Contient : `{ remoteId: string, localPath: string, dateAdded: DateTime }[]`
- Comparaison par `remoteId` entre l'index et les items distants

### 7. Fournisseurs de métadonnées locaux (ILocalMetadataProvider)

Emby ne lit pas les `.nfo` Kodi pour les types `Audio` (chapitres) et `Folder` dans une
bibliothèque Audiobooks, ni pour `Book`. Trois providers custom comblent ces lacunes :

| Provider | Type Emby | Fichier lu | Rôle |
|---|---|---|---|
| `AudioBookNfoProvider` | `Audio` | `album.nfo` (dossier parent) | Injecte `Album`, `AlbumArtists`, `ProductionYear` sur chaque chapitre |
| `AudioBookFolderNfoProvider` | `Folder` | `album.nfo` (dans le dossier) | Injecte titre, synopsis, genres, auteurs sur le container du livre |
| `BookNfoProvider` | `Book` | `{filename}.nfo` | Injecte les métadonnées complètes pour les ebooks |

Ces providers sont auto-découverts par Emby via le plugin assembly (pas de registration manuelle).

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
