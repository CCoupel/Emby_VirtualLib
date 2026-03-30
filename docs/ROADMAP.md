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

## Phase 1.5 — UI & Sync manuelle *(en cours)*

- [ ] Page de configuration HTML dans le dashboard Emby
- [ ] Formulaire ajout / édition / suppression d'un serveur source (URL + API key)
- [ ] Bouton "Tester la connexion" → `TestConnectionAsync`
- [ ] Listing des bibliothèques disponibles après connexion (`ListLibrariesAsync`)
- [ ] Sélection des bibliothèques à synchroniser (checkboxes → `ConnectorConfig.LibraryIds`)
- [ ] Bouton "Synchroniser maintenant" → génération `.strm` + `.nfo` + scan Emby
- [ ] Affichage progression / logs dans l'UI

---

## Phase 2a — Synchronisation des métadonnées

- [ ] `LibrarySyncJob` : tâche planifiée (`IScheduledTask`) configurable
- [ ] Détection delta : index JSON local `{connectorId}.json`
- [ ] Téléchargement artwork (poster, backdrop, thumb)
- [ ] Gestion des suppressions (items supprimés sur la source)
- [ ] Logs de sync dans le dashboard
- [ ] Tests intégration sync job

---

## Phase 2b — Proxy streaming

- [ ] `ProxyController` : endpoint `GET /virtuallib/proxy/{connectorId}/{itemId}`
- [ ] Support `Range` headers (seek / scrubbing)
- [ ] Mode proxy configurable à 3 niveaux :
  - Par serveur (`ConnectorConfig.ProxyMode`)
  - Par bibliothèque (override dans la config de lib)
  - Par média (override item-level)
- [ ] Modes : `Proxy` (passe par A) | `Direct` (URL source au client) | `Auto` (proxy si WAN, direct si LAN)
- [ ] Tests intégration ProxyController

---

## Phase 3 — Multi-connecteurs

- [ ] `JellyfinConnector` (API proche d'Emby)
- [ ] `PlexConnector` (API XML différente)
- [ ] Support N serveurs configurés simultanément
- [ ] UI multi-serveurs
