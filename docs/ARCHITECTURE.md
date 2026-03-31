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
    public string RemoteId { get; set; }        // ID natif sur le serveur source
    public string Title { get; set; }
    public MediaType Type { get; set; }         // Movie | Episode | Music | Photo
    public int? Year { get; set; }
    public string? SeriesName { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? ImdbId { get; set; }
    public string? TmdbId { get; set; }
    public DateTime? DateAdded { get; set; }
}

public class MediaMetadata : MediaItem
{
    public string? Overview { get; set; }
    public float? Rating { get; set; }
    public IEnumerable<string> Genres { get; set; }
    public IEnumerable<string> Studios { get; set; }
    public int? RuntimeMinutes { get; set; }
}

public enum MediaType { Movie, Episode, Music, Photo }
public enum ArtworkType { Poster, Backdrop, Thumb, Logo }
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
http://localhost:8096/virtuallib/proxy/{connectorId}/{remoteItemId}
```

L'utilisation de `localhost` garantit que le stream transite par A même si B est inaccessible au client.

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
GET /virtuallib/proxy/{connectorId}/{itemId}
```

**Le DTO doit être marqué `[Unauthenticated]`** : ffprobe probe les STRM sans token d'authentification. Sans cet attribut, Emby retourne 401 et la lecture échoue immédiatement.

**Comportement** :
1. Résout le connector correspondant à `connectorId`
2. Demande l'URL de stream à `connector.GetStreamUrlAsync(itemId)`
3. Ouvre un `HttpClient` vers cette URL
4. Copie les headers pertinents (Content-Type, Content-Length, Accept-Ranges, Content-Range)
5. Supporte les `Range` headers pour permettre le seek
6. Pipe le body vers la réponse client via `Stream.CopyToAsync`

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
  1. TestConnection() — skip si KO
  2. ListLibraries() — récupère les bibliothèques configurées
  3. Pour chaque bibliothèque :
     a. ListItems() — liste tous les items distants
     b. Calcule le delta (nouveaux / supprimés / inchangés)
     c. Pour les nouveaux : génère .strm + .nfo + télécharge artwork
     d. Pour les supprimés : supprime les fichiers locaux
  4. Déclenche un scan de la bibliothèque virtuelle sur Emby A
```

**Détection de delta** :
- Fichier index JSON local : `{virtualLibRoot}/.index/{connectorId}.json`
- Contient : `{ remoteId: string, localPath: string, dateAdded: DateTime }[]`
- Comparaison par `remoteId` entre l'index et les items distants

---

### 7. PluginConfiguration

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
         contenu: http://localhost:8096/virtuallib/proxy/emby-b/12345
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
      → GET http://A:8096/virtuallib/proxy/emby-b/12345
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
- Le ProxyController ne proxifie que les items présents dans l'index local (pas de proxy ouvert)
- Validation de l'itemId avant proxy : doit exister dans `{connectorId}.json`

---

## Extensibilité future

Pour ajouter un nouveau type de serveur :
1. Créer `PlexConnector : IMediaServerConnector`
2. Implémenter la traduction API Plex XML → modèles normalisés
3. Enregistrer dans le conteneur DI du plugin
4. Ajouter `"Plex"` dans la liste des `ServerType` disponibles dans l'UI

Le core (StrmGenerator, NfoGenerator, ProxyController, LibrarySyncJob) ne change pas.
