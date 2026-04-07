# Roadmap — VirtualLib

## Documentation

- Architecture & Core → [core/](./core/)
- Connecteurs → [connectors/](./connectors/)
- Types de médias → [media-types/](./media-types/)
- Configuration utilisateur → [CONFIGURATION.md](./CONFIGURATION.md)

---

## Phase 1 — Core ✅ Terminé

- [x] Scaffolding projet C# plugin Emby
- [x] Interface `IMediaServerConnector` et modèles normalisés
- [x] `EmbyConnector` : auth, list libraries, list items, metadata
- [x] `StrmGenerator` : génération fichiers .strm
- [x] `NfoGenerator` : génération fichiers .nfo (movie + episode)
- [x] `PluginConfiguration` + `ConnectorConfig`
- [x] `Plugin.cs` entry point — plugin chargé et vérifié sur serveur de test
- [x] 18 tests unitaires (xUnit + Moq)

---

## Phase 1.5 — UI & Sync manuelle ✅ Terminé

- [x] Page de configuration HTML dans le dashboard Emby
- [x] Formulaire ajout / édition / suppression d'un serveur source (URL + API key)
- [x] Bouton "Tester la connexion" → `TestConnectionAsync`
- [x] Listing des bibliothèques disponibles après connexion (`ListLibrariesAsync`)
- [x] Sélection des bibliothèques à synchroniser (checkboxes → `ConnectorConfig.LibraryIds`)
- [x] Bouton "Synchroniser maintenant" → génération `.strm` + `.nfo` + scan Emby
- [x] Paramètre `ProxyBaseUrl` (override de l'URL de base pour les `.strm`)

---

## Phase 2a — Synchronisation des métadonnées ✅ Terminé (v1.1.0)

- [x] `SyncService` : orchestration sync par connector + bibliothèque
- [x] Génération `.strm` + `.nfo` + téléchargement artwork (poster, fanart, landscape, logo)
- [x] Métadonnées complètes : cast, directors, writers, tagline, trailer URL
- [x] Skip intelligent : toujours régénérer le `.strm`, ne sauter le `.nfo` qu'en mode `RemoteSync`
- [x] `MetadataMode` par connecteur : `RemoteSync` (incrémental) / `RemoteSyncFull` (force) / `LocalScraping`
- [x] `LibraryOptions` Emby appliquées selon le mode (fetchers TMDB/TVDB/FanArt, cache, chapitres)
- [x] `LibrarySyncJob` : tâche planifiée (`IScheduledTask`) avec intervalle configurable
- [x] Mise à jour dynamique du trigger sans redémarrage
- [x] `QueueLibraryScan()` déclenché si des items ont été créés
- [x] Compteurs d'items distants par bibliothèque (endpoint `/item-counts`)
- [x] Fix `Users/Me` 500 : `GetUserIdAsync` sans appel à `/Users/Me`
- [x] Progression sync par librairie dans l'UI (itération client-side)

**Reste en backlog :**
- [ ] Détection delta : index JSON local `{connectorId}.json` (issue #12)
- [ ] Gestion des suppressions (items supprimés sur la source)
- [ ] Tests intégration sync job (issue #14)

---

## Phase 2b — Proxy streaming ✅ Terminé

- [x] `ProxyController` : endpoint `GET /virtuallib/proxy/{connectorId}/{itemId}`
- [x] Support `Range` headers (seek / scrubbing)
- [x] Forward `Content-Range` et `Accept-Ranges` (obligatoire pour que ffprobe détecte la taille réelle)
- [x] `[Unauthenticated]` sur le DTO ProxyStreamRequest (ffprobe probe sans token)
- [x] Gestion propre des déconnexions client (broken pipe, OperationCanceledException)
- [x] Lecture validée end-to-end : web + app, direct play + transcodage

---

## Phase 2c — Sécurité proxy & nettoyage ✅ Terminé (v1.2.0)

- [x] Redesign URL proxy : `/virtuallib/proxy/{connectorId}/{libraryId}/{itemId}` (ajout `libraryId`)
- [x] Validation connecteur actif + bibliothèque activée avant de proxifier
- [x] Contrôle d'accès token : vérification droits utilisateur sur la bibliothèque virtuelle Emby
- [x] Blocage des requêtes navigateur sans token (filtre User-Agent : `Lavf/*` et absent = interne, `Mozilla/*` = navigateur)
- [x] Suppression automatique des fichiers `.strm`/`.nfo` quand une bibliothèque est décochée ou un connecteur supprimé
- [x] Fix DI : `ILogger<ProxyController>` retiré du constructeur (non enregistré dans Emby SimpleInjector)

**Limites connues :**
- Emby ne transmet pas le token utilisateur lors des appels ffprobe internes → contrôle d'accès par token impossible côté proxy pour les requêtes server-side ; délégué à Emby (`PlaybackInfo`) qui est le vrai point de contrôle

---

## Phase 3 — Connecteur Plex ✅ Terminé (v1.3.0)

- [x] `PlexConnector` : auth par API key, API XML `/library/sections`, items avec métadonnées (films + séries)
- [x] `PlexTvConnector` : authentification via plex.tv, sélection du serveur par machineIdentifier
- [x] Résolution automatique de la meilleure URL (locale → plex.direct → relay), exclusion des IPs LAN inaccessibles depuis Kubernetes
- [x] Timeout 120 s sur les connexions relay (latence élevée)
- [x] Token d'accès par serveur (`accessToken` depuis `/api/v2/resources`, distinct du token global plex.tv)
- [x] Support 2FA (code TOTP transmis au moment du chargement des serveurs)
- [x] Compteur d'items distants Plex : paramètre `X-Plex-Container-Start=0` requis pour obtenir `totalSize`
- [x] Refonte UI : arbre collapsible par type de médiathèque (Movies, TvShows…), trié A→Z, replié par défaut
- [x] Compteurs résumés sur chaque ligne connecteur et chaque groupe de type (X/Y libs · A/B items)
- [x] Auto-découverte des nouvelles bibliothèques lors du sync (merge dans `KnownLibraries`)
- [x] Rafraîchissement automatique de l'UI après sync (sans rechargement de page)
- [x] Pré-remplissage du mot de passe en édition de connecteur (évite de devoir le ressaisir pour Test Connection)
- [x] Mise à jour du compteur d'items distants pour toutes les bibliothèques (cochées et non cochées) lors du sync

**Reste en backlog :**
- [ ] `JellyfinConnector` (API proche d'Emby) — issue #15
- [ ] Détection delta : index JSON local `{connectorId}.json` — issue #12
- [ ] Gestion des suppressions (items supprimés sur la source)

---

## Phase 4 — Livres audio & ebooks ✅ Terminé (v1.4.0 → v1.5.0)

### v1.4.0 — Support initial livres audio, ebooks, photos
- [x] `MediaType.AudioBook` et `MediaType.Book` ajoutés
- [x] `StrmGenerator` : arborescence `{livre}/{chapitre}.strm` pour les livres audio
- [x] `EpubStubGenerator` : téléchargement du vrai fichier epub/pdf/mobi depuis la source
- [x] `NfoGenerator.GenerateAudioBookNfo()` : fichier `album.nfo` au format Music/AudioBook Emby
- [x] `LibraryProvisioner` : création automatique des dossiers virtuels Emby (`audiobooks`, `books`, `photos`)
- [x] Support photos/homevideos dans le connector Emby

### v1.5.0 — Métadonnées livres audio robustes
- [x] `AudioBookNfoProvider` (`ILocalMetadataProvider<Audio>`) : lit `album.nfo` pour chaque chapitre et injecte `Album`, `AlbumArtists`, `ProductionYear`
- [x] `AudioBookFolderNfoProvider` (`ILocalMetadataProvider<Folder>`) : lit `album.nfo` pour le container du livre
- [x] `BookNfoProvider` (`ILocalMetadataProvider<Book>`) : lit `{filename}.nfo` pour les ebooks
- [x] `MediaItem.RuntimeTicks` : durée en ticks 100 ns (propagée depuis `EmbyItem.RunTimeTicks`)
- [x] `MediaItem.AlbumArtists` : auteurs propagés depuis `AlbumArtist` / `People[Author]` du serveur distant
- [x] Injection directe en DB (`ILibraryManager.UpdateItem`) : `RunTimeTicks`, `Album`, `AlbumArtists` sur les items `Audio` — contourne le ffprobe différé d'Emby sur les `.strm`
- [x] **Polling loop post-scan** : boucle background (2 s, timeout 5 min) qui attend que le scan Emby crée les items en DB, puis injecte les métadonnées — réduit à 1 sync unique (plus besoin de 2 syncs)
- [x] Artwork découplé de la condition NFO : images téléchargées même quand `album.nfo` existe déjà
- [x] Fallback artwork : si le container AudioBook n'a pas d'image, utilise l'artwork du chapitre
- [x] `ArtworkType` étendu : `Banner`, `Disc`, `Art` (ClearArt) en plus de Poster/Backdrop/Thumb/Logo
- [x] Images par chapitre : Primary téléchargée comme `{chapitre}.jpg` à côté du `.strm`
- [x] Fix `AudioBookNfoProvider` : injecte `Album` (titre du livre) et non `Name` sur les chapitres

---

## Phase 5 — Synchronisation des états utilisateur ✅ Terminé (v1.6.0)

- [x] **Champs utilisateur sur `MediaItem`** : `IsPlayed`, `IsFavorite`, `PlayCount`, `LastPlayedDate`, `PlaybackPositionTicks`
- [x] **EmbyConnector** : champ `UserData` ajouté dans les requêtes `ListItemsAsync` et `GetMetadataAsync` ; mapping vers `MediaItem`
- [x] **PlexConnector** : parsing `viewCount` (lu/playCount), `viewOffset` (position en ms → ticks), `lastViewedAt` dans `MapVideoToItem` et `MapVideoToMetadata`
- [x] **`SyncUserFlags`** (Phase 2) : après injection des métadonnées, applique les états lus/favoris/position pour tous les utilisateurs locaux via `IUserDataManager.SaveUserData(..., UserDataSaveReason.Import)`
- [x] **`SyncUserFlagsForFolder`** (Phase 1) : sync des flags show/saison directement dans le dossier (ces items ne passent pas par `pendingStrms`)
- [x] **`LibrarySyncJob` + `ConfigController`** : injection de `IUserDataManager` et `IUserManager` dans `SyncService`
- [x] Stratégie merge : les états locaux ne sont jamais réduits (seule l'augmentation est propagée — playCount, position, favori)
- [x] Compatibilité : `IUserManager.Users` (déprécié mais seul accès disponible dans Emby) protégé par `#pragma warning disable CS0618`
- [x] `UserDataSaveReason` : alias `EmbyUserDataSaveReason = MediaBrowser.Model.Entities.UserDataSaveReason` pour éviter l'ambiguïté `MediaType`

**Reste en backlog :**
- [ ] Détection delta / suppressions — issue #12
- [ ] `JellyfinConnector` — issue #15

---

## Phase 6 — Sync parallèle & UI temps réel ✅ Terminé (v1.6.1)

- [x] **Sync 100 % parallèle** : toutes les bibliothèques de tous les connecteurs synchées simultanément (`Task.WhenAll`) — issues #30 et #35
- [x] **Phase 2 autonome** : chaque bibliothèque enchaîne Phase 1 → Phase 2 de façon indépendante, sans attendre les autres
- [x] **`MaxParallelLibraries`** : limite configurable par connecteur (défaut : 4), appliquée via `SemaphoreSlim` sur Phase 1 uniquement (Phase 2 = polling Emby, pas de charge réseau distante)
- [x] **`SyncState`** redesigné : `ConcurrentDictionary<string, LibrarySyncEntry>` avec champs `Volatile.Read/Write` pour la thread-safety, statuts `Pending / RunningPhase1 / RunningPhase2 / Done / Failed`
- [x] **Barres de progression inline** : double barre (Phase 1 bleue / Phase 2 verte) sur chaque ligne de l'arbre (bibliothèque, type, connecteur), affichage via polling HTTP `/virtuallib/sync/status` toutes les 2 s
- [x] **Barre globale** dans le header "Remote Connectors", compteur à droite
- [x] **Barres pleine largeur** : s'étirent sur tout l'espace disponible (`flex:1`) — même dénominateur (`p1Total`) pour les deux phases, évite les sauts de progression
- [x] **Épaisseur par niveau** : connecteur 9 px / type 6 px / bibliothèque 4 px
- [x] **Pistes bicolores** : fond clair (20 % opacité) indique le total, remplissage foncé indique l'avancement

---

## Phase 8 — Organisation des bibliothèques (SharedByType) ✅ Terminé (v1.8.0) — issue #36

### Fonctionnalité
- [x] **`LibraryOrganization`** enum par connecteur : `Isolated` (défaut) / `SharedByType`
  - `Isolated` : une médiathèque Emby dédiée par paire connecteur–bibliothèque (`ConnectorName — LibraryName`)
  - `SharedByType` : une médiathèque Emby partagée par type de contenu (Movies, TvShows…), chaque bibliothèque distante y ajoute son propre chemin
- [x] **`SharedLibraryPrefix` / `SharedLibrarySuffix`** (paramètres globaux) : personnalisation du nom de la médiathèque partagée (ex. préfixe `[VL] ` → `[VL] Movies`)
- [x] **Cohérence du chemin physique** : les fichiers `.strm`/`.nfo` sont toujours placés dans `virtualLibRoot/ConnectorName/LibraryName/`, **identique** pour les deux modes — le mode d'organisation ne change que le nom de la médiathèque Emby, pas la structure sur disque
- [x] **Ajout incrémental des chemins** (`AddMediaPaths`) : chaque bibliothèque distante ajoute son chemin individuellement à la médiathèque partagée existante
- [x] **Suppression sélective** : en mode SharedByType, la suppression d'une bibliothèque retire uniquement son chemin via `RemoveMediaPath(long itemId, string path)` ; la médiathèque partagée n'est supprimée que si aucun connecteur ne l'utilise plus (`NoRemainingSharedLibraries`)
- [x] **Bouton "Cancel Sync"** : annulation d'une synchronisation en cours depuis l'UI

### Corrections d'API Emby découvertes pendant l'implémentation
- [x] **`ApplyLibraryOptions` préserve les `PathInfos`** : `UpdateLibraryOptions` réinitialise la liste des chemins si on ne les ré-injecte pas explicitement — `_libraryManager.GetLibraryOptions(collectionFolder)?.PathInfos` doit être préservé avant chaque appel
- [x] **`RemoveMediaPath(long, string)`** : signature réelle Emby — premier argument = itemId (`long`), pas de paramètre `refreshLibrary` (contrairement à `AddMediaPaths` / `RemoveVirtualFolder`)

---

## v1.8.2 — Fix perte des MediaStream lors d'un scan manuel ✅ Terminé

- [x] **`StrmGenerator` idempotent** : le `.strm` n'est écrit que si son contenu change (URL différente ou fichier absent). Évite de modifier le `mtime`, ce qui déclenchait un re-scan Emby → `SaveMediaStreams([])` → perte des infos codec/résolution/audio en DB lors de chaque "Scan library files" manuel entre deux syncs VirtualLib.

---

## Phase 7.1 — Isolation multi-utilisateur de la lecture ✅ Terminé (v1.8.1) — issue #41

- [x] **`sessionKey` inclut le `userId` local** : `{connectorId}:{remoteItemId}:{localUserId}` — élimine la race condition quand 2 users regardent le même item simultanément
- [x] **`LocalUserId` dans `ConnectorConfig`** : champ permettant de lier un connecteur à un user local spécifique ; sélecteur dans l'UI de configuration
- [x] **Sessions isolées sur le distant** : `playSessionId` utilisé comme `DeviceId` dans `X-Emby-Authorization` → chaque stream apparaît comme une session distincte sur le dashboard du serveur distant
- [x] **Identité dynamique** : `deviceName` au format `user@client` (ex: `cyril@Emby Web`) propagé dans tous les appels playback (Emby + Plex)
- [x] **Isolation des Progress** : seul le user lié (A) envoie ses Progress au distant ; les autres users (B) maintiennent leur position localement (évite l'oscillation de `UserData.PlaybackPositionTicks`)
- [x] **Restauration de position au Stop(B)** (`ResolveLinkedUserPosition`) : au Stop d'un user non-lié, on envoie la position de A (depuis session active ou `IUserDataManager.GetUserData`) au lieu de 0 — préserve le resume point de A sur le serveur distant
- [x] **`SyncUserFlags` ciblé** : sync des états vu/favori/position uniquement vers le user lié (`LocalUserId`), plus vers tous les users locaux

---

## Phase 7 — Backpropagation de la lecture ✅ Terminé (v1.7.0) — issue #34

- [x] **`PlaybackEventForwarder`** (`IServerEntryPoint`) : s'abonne aux events `ISessionManager` (Start / Progress / Stop) et propage les notifications vers le serveur distant via le connecteur correspondant
- [x] **Détection du fichier `.strm`** : parse l'URL proxy `{baseUrl}/virtuallib/proxy/{connectorId}/{libraryId}/{remoteItemId}` pour identifier le connecteur et l'item distant
- [x] **Heartbeat 30 s** : maintient la session remote vivante pendant les pauses prolongées (les clients Emby cessent d'envoyer des Progress après buffer, le remote killait la session en ~60 s)
- [x] **Debounce Stop 8 s** : le host Emby fire `PlaybackStopped` quand la connexion HTTP proxy se ferme (buffer client plein), même si l'utilisateur est encore en pause. On attend 8 s — si un Progress arrive avant, le Stop est annulé
- [x] **Fix race condition** : le debounce Stop vérifie le `PlaySessionId` courant avant de supprimer (évite de tuer une nouvelle session démarrée entre-temps)
- [x] **Réouverture transparente** : un `Progress` pour une session absente (`_sessions`) ré-envoie automatiquement Start + Progress (couvre debounce expiré, pod restart, host ne renvoyant pas de `PlaybackStart`)
- [x] **`PositionTicks` dans Stopped** : la position finale est envoyée au remote pour sauvegarder l'avancement ("Continuer la lecture")
- [x] **Fix `PostWithRetryAsync`** (`EmbyConnector`) : utilise `StringContent` avec `Content-Length` explicite au lieu de `PostAsJsonAsync` (chunked) — ServiceStack (Emby) ignore les corps chunked, causant des champs null dont `PlaySessionId`
- [x] **`PlaySessionId`** : GUID généré à chaque Start, propagé dans Progress et Stopped (évitait un `ArgumentNullException` dans `SessionInfo.GetOrAddPlaySessionInfo`)
- [x] **Reporting Plex** (`PlexConnector`) : `GET /:/timeline?state=playing|paused|stopped&time={ms}` + `GET /:/progress?key={ratingKey}&time={ms}` — le `viewOffset` est persité en base pour "Continuer la lecture" (Plex ne persiste pas via `/:/timeline` seul)
- [x] **Reporting Emby** (`EmbyConnector`) : `POST Sessions/Playing`, `Sessions/Playing/Progress`, `Sessions/Playing/Stopped` avec `PlaySessionId`, `PositionTicks`, `UserId`
