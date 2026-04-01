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

## Phase 3 — Multi-connecteurs

- [ ] `JellyfinConnector` (API proche d'Emby)
- [ ] `PlexConnector` (API XML différente)
- [ ] Support N serveurs configurés simultanément
- [ ] UI multi-serveurs
