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

## Phase 2a — Synchronisation des métadonnées *(en cours)*

- [x] `SyncService` : orchestration sync par connector + bibliothèque
- [x] Génération `.strm` + `.nfo` + téléchargement artwork
- [x] Skip intelligent : toujours régénérer le `.strm` (cheap), ne sauter que si `.nfo` déjà présent
- [ ] `LibrarySyncJob` : tâche planifiée (`IScheduledTask`) configurable
- [ ] Détection delta : index JSON local `{connectorId}.json`
- [ ] Gestion des suppressions (items supprimés sur la source)
- [ ] Logs de sync dans le dashboard
- [ ] Tests intégration sync job

---

## Phase 2b — Proxy streaming ✅ Terminé

- [x] `ProxyController` : endpoint `GET /virtuallib/proxy/{connectorId}/{itemId}`
- [x] Support `Range` headers (seek / scrubbing)
- [x] Forward `Content-Range` et `Accept-Ranges` (obligatoire pour que ffprobe détecte la taille réelle)
- [x] `[Unauthenticated]` sur le DTO ProxyStreamRequest (ffprobe probe sans token)
- [x] Gestion propre des déconnexions client (broken pipe, OperationCanceledException)
- [x] Lecture validée end-to-end : web + app, direct play + transcodage

---

## Phase 3 — Multi-connecteurs

- [ ] `JellyfinConnector` (API proche d'Emby)
- [ ] `PlexConnector` (API XML différente)
- [ ] Support N serveurs configurés simultanément
- [ ] UI multi-serveurs
