# Roadmap — VirtualLib

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
- [ ] Progression sync par item en temps réel (issue #20)

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
- [ ] `JellyfinConnector` (API proche d'Emby)
- [ ] Détection delta : index JSON local `{connectorId}.json` (issue #12)
- [ ] Gestion des suppressions (items supprimés sur la source)
- [ ] Progression sync par item en temps réel (issue #20)

---

## Phase 4 — Livres audio & ebooks ✅ Terminé (v1.4.0 → v1.5.0)

### v1.4.0 — Support initial livres audio, ebooks, photos
- [x] `MediaType.AudioBook` et `MediaType.Book` ajoutés
- [x] `StrmGenerator` : arborescence `{livre}/{chapitre}.strm` pour les livres audio
- [x] `EpubStubGenerator` : téléchargement du vrai fichier epub/pdf/mobi depuis la source
- [x] `NfoGenerator.GenerateAudioBookNfo()` : fichier `album.nfo` au format Music/AudioBook Emby
- [x] `LibraryProvisioner` : création automatique des dossiers virtuels Emby (`audiobooks`, `books`, `photos`)
- [x] Support photos/homevideos dans le connector Emby

### v1.5.0 — Métadonnées livres audio robustes (branche `feature/audiobook-book-nfo-provider`)
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

**Reste en backlog :**
- [ ] Détection delta / suppressions
- [ ] `JellyfinConnector`
