# Architecture — VirtualLib Plugin

## Vue d'ensemble

VirtualLib est un plugin Emby qui expose les bibliothèques de serveurs médias distants comme des bibliothèques natives sur le serveur hôte. Il repose sur trois mécanismes :

1. **Sync** — interroge périodiquement les serveurs sources pour construire une arborescence de fichiers `.strm` + `.nfo`
2. **Index** — Emby indexe cette arborescence comme une bibliothèque normale
3. **Proxy** — un endpoint HTTP intégré au plugin proxifie le stream depuis la source, permettant le transcodage par le serveur hôte

```
Client Emby
    │
    ▼
Emby Server A (hôte)
    ├── Bibliothèques locales (normales)
    ├── Bibliothèques virtuelles (générées par le plugin)
    │       ├── Films_ServerB/
    │       │     ├── Inception (2010).strm  → http://A/virtuallib/proxy/emby-b/lib456/12345
    │       │     └── movie.nfo
    │       └── Séries_ServerB/
    │
    └── Plugin VirtualLib
            ├── IMediaServerConnector (interface)
            ├── EmbyConnector / PlexConnector
            ├── StrmGenerator + NfoGenerator
            ├── SyncService + LibrarySyncJob
            └── ProxyController
                    │
                    └──► Serveur B (source) — stream + metadata
```

---

## Composants

| Composant | Rôle | Documentation |
|---|---|---|
| `IMediaServerConnector` | Abstraction serveur source | [connectors/SPEC.md](../connectors/SPEC.md) |
| `EmbyConnector` | Connecteur Emby/Jellyfin | [connectors/EMBY.md](../connectors/EMBY.md) |
| `PlexConnector` | Connecteur Plex | [connectors/PLEX.md](../connectors/PLEX.md) |
| `StrmGenerator` + `NfoGenerator` | Génération fichiers locaux | [SYNC.md](./SYNC.md) |
| `SyncService` + `LibrarySyncJob` | Orchestration sync + états utilisateur | [SYNC.md](./SYNC.md) |
| `ProxyController` | Proxy streaming | [PROXY.md](./PROXY.md) |
| `AudioBookNfoProvider` etc. | Providers métadonnées locaux | [media-types/AUDIOBOOK_BOOK.md](../media-types/AUDIOBOOK_BOOK.md) |
| Référence API Emby REST | Endpoints utilisés | [API_EMBY.md](./API_EMBY.md) |

---

## Flux de données

### Lors de la sync

```
LibrarySyncJob
  → SyncService.SyncConnectorAsync()
      → Connector.ListItemsAsync()
          ← items[] (avec TechnicalInfo + états utilisateur)
      → StrmGenerator.Generate(item)
          → écrit .strm (URL proxy)
      → NfoGenerator.Generate(metadata)
          → écrit .nfo avec <fileinfo><streamdetails>
      → ArtworkDownloader.Download(item)
          → écrit poster.jpg, fanart.jpg
      → QueueLibraryScan()
  → SyncService.PushMetadataAsync()  [polling loop post-scan]
      → FindByPath(strmPath) → item en DB ?
      → UpdateItem() + SaveMediaStreams() + SyncUserFlags()
```

### Lors de la lecture

```
Client Emby
  → browse → Emby A sert depuis son index local (.nfo)
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

## PluginConfiguration

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    public List<ConnectorConfig> Connectors { get; set; } = new();
    public string VirtualLibraryRootPath { get; set; } = "";
    public int SyncIntervalHours { get; set; } = 6;
    public int ProxyTimeoutSeconds { get; set; } = 30;
    // Override de l'URL de base pour les .strm (utile derrière un reverse proxy)
    public string ProxyBaseUrl { get; set; } = string.Empty;
}

public class ConnectorConfig
{
    public string Id { get; set; }              // GUID unique
    public string DisplayName { get; set; }
    public string ServerType { get; set; }      // "Emby" | "Plex"
    public string ServerUrl { get; set; }
    public string ApiKey { get; set; }
    public List<string> LibraryIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
}
```

---

## Considérations de sécurité

- Les API keys des serveurs sources sont stockées dans la config Emby (chiffrée sur disque)
- Les tokens ne sont jamais exposés dans les URLs `.strm` (tout passe par le proxy)
- Le ProxyController valide connector + libraryId avant de proxifier
- Accès navigateur sans token bloqué par filtre User-Agent
- Accès utilisateur avec token → vérification droits sur la bibliothèque virtuelle Emby
- Le vrai point de contrôle pour les requêtes internes (ffprobe) est Emby via `PlaybackInfo`

Détails complets → [PROXY.md](./PROXY.md).

---

## Extensibilité

Pour ajouter un nouveau connecteur (ex: Jellyfin — issue #15) :
1. Créer `JellyfinConnector : IMediaServerConnector` (API très proche d'Emby — voir [connectors/EMBY.md](../connectors/EMBY.md))
2. Implémenter la traduction API → modèles normalisés (dont états utilisateur)
3. Enregistrer dans `ConnectorFactory` ([connectors/SPEC.md](../connectors/SPEC.md))
4. Ajouter `"Jellyfin"` dans la liste `ServerType` de l'UI

Le core (StrmGenerator, NfoGenerator, ProxyController, SyncService) ne change pas.
