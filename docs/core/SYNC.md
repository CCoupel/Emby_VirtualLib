# Synchronisation — SyncService & LibrarySyncJob

## Orchestration parallèle (v1.6.1)

Toutes les bibliothèques activées de tous les connecteurs sont synchronisées **simultanément** (`Task.WhenAll`). Chaque bibliothèque est traitée de façon autonome et indépendante.

```
SyncAll / LibrarySyncJob.Execute()
  │
  ├── ConnectorA :  SemaphoreSlim(MaxParallelLibraries=4)
  │     ├── LibA1  → Phase1 ──(sémaphore relâché)──► Phase2
  │     ├── LibA2  → Phase1 ──(sémaphore relâché)──► Phase2
  │     └── LibA3  → Phase1 ──(sémaphore relâché)──► Phase2
  │
  └── ConnectorB :  SemaphoreSlim(MaxParallelLibraries=4)
        └── LibB1  → Phase1 ──(sémaphore relâché)──► Phase2
```

- Le `SemaphoreSlim` limite la Phase 1 (appels réseau distants) mais **pas** la Phase 2 (polling Emby local).
- `SyncState` (ConcurrentDictionary) centralise les compteurs Phase1/Phase2 de chaque bibliothèque pour le polling UI.
- Statuts : `Pending → RunningPhase1 → RunningPhase2 → Done | Failed`

---

## Algorithme en deux phases

```
Phase 1 — SyncService.SyncConnectorAsync()
  Pour chaque ConnectorConfig actif :
    1. TestConnection() — abandon si KO
    2. ListLibraries() — merge auto-découverte dans KnownLibraries
    3. Pour chaque bibliothèque configurée :
       a. ListItems() — tous les items (avec TechnicalInfo + états utilisateur)
       b. Pour chaque item :
          - Génère toujours le .strm (idempotent, change la date de modif → déclenche rescan)
          - Si NFO absent ou RemoteSyncFull :
              · GetMetadataAsync() → MediaMetadata (avec Technical + états utilisateur)
              · Génère .nfo avec <fileinfo><streamdetails>
              · Télécharge artwork (poster.jpg, fanart.jpg…)
          - Si NFO présent (RemoteSync skip) :
              · PatchStreamDetails() si <fileinfo> absent
          - Pour épisodes :
              · GetMetadataAsync(SeriesId) → tvshow.nfo + artwork show (1x/série)
                SyncUserFlagsForFolder(showFolder, showMeta)
              · GetMetadataAsync(SeasonId) → season.nfo + artwork saison (1x/saison)
                SyncUserFlagsForFolder(seasonFolder, seasonMeta)
          - Ajoute à pendingStrms[]
    4. QueueLibraryScan()

Phase 2 — SyncService.PushMetadataAsync()
  Boucle de polling (toutes les 2 s, timeout 5 min) :
  Pour chaque item dans pendingStrms :
    · FindByPath(strmPath) → item en DB ?
    · Si oui → UpdateItem() (RunTimeTicks, Size, TotalBitrate, Container, Width, Height)
              → SaveMediaStreams() (codec/résolution injectés directement en DB)
              → PatchStreamDetails() (idempotent — complémente la phase 1)
              → SyncUserFlags() (played/favorite/position pour tous les users locaux)
    · Si non → réessaie au prochain tour
```

### Pourquoi deux phases ?

Emby ne lance pas ffprobe sur les `.strm` (fichiers HTTP). Sans injection :
- Durée inconnue → impossible de scrobbler / afficher la progression
- Pas de MediaStream → pas de résolution/codec affichés

Phase 1 (NFO `<fileinfo>`) crée les `MediaStream` lors du scan Emby.
Phase 2 (`UpdateItem` + `SaveMediaStreams`) complète `RunTimeTicks`, `Size`, et les streams directement sur l'item en DB.

### Génération idempotente du .strm

Le `.strm` n'est écrit que si son contenu change (URL différente ou fichier absent). Si l'URL est inchangée, le fichier n'est pas touché → son `mtime` reste stable → Emby ne le re-scanne pas lors d'un scan manuel.

**Pourquoi c'est important :** quand Emby détecte un fichier `.strm` modifié lors d'un "Scan library files", il tente de le re-prober via ffprobe (échec), puis appelle `SaveMediaStreams([])` → les entrées codec/résolution en DB sont effacées. En rendant la génération idempotente, les scans manuels entre deux syncs VirtualLib ne corrompent plus les MediaStream injectés.

---

## StrmGenerator

Génère les fichiers `.strm` dans l'arborescence de la bibliothèque virtuelle.

**URL dans le .strm** — pointe vers le ProxyController sur le serveur hôte :
```
https://media.example.com/virtuallib/proxy/{connectorId}/{libraryId}/{remoteItemId}
```

Le `libraryId` est inclus pour permettre la validation côté proxy (bibliothèque active + droits utilisateur).
L'URL est configurée via `ProxyBaseUrl`. Si vide, auto-détectée via headers `X-Forwarded-*`.

Arborescences par type de média :
- Films et séries → [media-types/VIDEO.md](../media-types/VIDEO.md)
- Livres audio et ebooks → [media-types/AUDIOBOOK_BOOK.md](../media-types/AUDIOBOOK_BOOK.md)

---

## NfoGenerator

Génère les fichiers `.nfo` au format Kodi/Emby pour éviter le re-scraping.

**Méthodes disponibles** :

| Méthode | Fichier généré | Élément racine XML |
|---|---|---|
| `GenerateMovieNfo` | `movie.nfo` | `<movie>` |
| `GenerateEpisodeNfo` | `{Series} - S{xx}E{yy}.nfo` | `<episodedetails>` |
| `GenerateShowNfo` | `tvshow.nfo` | `<tvshow>` |
| `GenerateSeasonNfo` | `season.nfo` | `<season>` |
| `GenerateAudioBookNfo` | `album.nfo` | `<album>` |
| `GenerateBookNfo` | `{Title} ({Year}).nfo` | `<book>` |
| `PatchStreamDetails` | modifie un NFO existant | — injecte `<fileinfo>` |

Formats XML détaillés → [media-types/VIDEO.md](../media-types/VIDEO.md) et [media-types/AUDIOBOOK_BOOK.md](../media-types/AUDIOBOOK_BOOK.md).

### Contrainte critique — `<fileinfo>` et fichiers `.strm`

Emby ne lance pas ffprobe sur les `.strm`. Sans `<fileinfo>` dans le NFO, aucun `MediaStream` n'est créé → pas de résolution, pas de codec, pas de durée affichée.

`UpdateItem(video.Width = 1920)` **ne crée pas de MediaStream** — seul le scan du NFO avec `<fileinfo>` crée des entrées `MediaStream` en DB.

### Stratégie en deux temps

1. **Phase 1 (avant scan)** : NFO généré avec `<fileinfo>` d'emblée pour les nouveaux items. Pour les items existants (skip), `PatchStreamDetails` injecte `<fileinfo>` si absent.
2. **Phase 2 (après scan)** : `UpdateItem` injecte `RunTimeTicks`, `Size`, `TotalBitrate`. `SaveMediaStreams` injecte les streams en DB. `PatchStreamDetails` rappelé en cas de NFO non encore patché.

`PatchStreamDetails` est idempotent (no-op si `<fileinfo>` déjà présent ou données nulles).

---

## Synchronisation des états utilisateur (v1.6.0)

Les états de lecture (lu, favori, position) sont propagés depuis le serveur source vers la bibliothèque virtuelle locale pour **tous les utilisateurs locaux**.

### Stratégie de merge

Les états ne sont jamais réduits — seule l'augmentation est propagée :

| Champ | Règle |
|---|---|
| `Played` | Set à `true` si source = true ; jamais remis à false |
| `PlayCount` | Prend le max(local, source) |
| `LastPlayedDate` | Prend la date la plus récente |
| `IsFavorite` | Set à `true` si source = true ; jamais désactivé |
| `PlaybackPositionTicks` | Prend la position la plus avancée |

### Implémentation

- **`SyncUserFlags`** (Phase 2) : appelé pour chaque item individuel après `SaveMediaStreams`. Utilise `IUserDataManager.SaveUserData(..., UserDataSaveReason.Import)`.
- **`SyncUserFlagsForFolder`** (Phase 1) : appelé pour les dossiers show/saison qui ne passent pas par `pendingStrms`.
- `IUserManager.Users` (déprécié dans Emby mais seul accès disponible) — protégé par `#pragma warning disable CS0618`.
- `UserDataSaveReason` : alias `EmbyUserDataSaveReason = MediaBrowser.Model.Entities.UserDataSaveReason` pour éviter l'ambiguïté avec `MediaType`.

### Source des données par connecteur

| Connecteur | Source des états utilisateur |
|---|---|
| **Emby** | Champ `UserData` dans `GET /Users/{userId}/Items` — nécessite mode **User Credentials** (API Key retourne les données de l'admin, souvent toutes à zéro) |
| **Plex** | Attributs XML `viewCount`, `viewOffset` (ms), `lastViewedAt` (unix timestamp) sur `<Video>` |

### Limitation

La lecture depuis VirtualLib **n'est pas rapportée** au serveur source. La backpropagation temps réel est prévue dans une future version (issue #34).
