# VirtualLib — Emby Plugin

## Vision du projet

Plugin Emby permettant d'agréger des bibliothèques de serveurs médias distants (Emby, Jellyfin, Plex) comme bibliothèques virtuelles natives sur un serveur Emby hôte. Le serveur hôte sert de proxy transparent pour le streaming et le transcodage.

## Architecture générale

```
Client Emby
    │
    ▼
Emby Server A (hôte)
    ├── Bibliothèques locales (normales)
    ├── Bibliothèques virtuelles (générées par le plugin)
    │       ├── Films_ServerB/
    │       │     ├── Inception (2010).strm  → http://A/virtuallib/proxy/emby-b/12345
    │       │     └── Inception (2010).nfo
    │       └── Séries_ServerB/
    │
    └── Plugin VirtualLib
            ├── IMediaServerConnector (interface)
            ├── EmbyConnector (implémentation)
            ├── StrmGenerator
            ├── NfoGenerator
            ├── LibrarySyncJob (scheduled task)
            └── ProxyController (endpoint HTTP proxy)
                    │
                    └──► Serveur B (source) — stream + metadata
```

Le flux de lecture :
1. Client browse → Emby A sert les métadonnées depuis les .nfo locaux
2. Client play → requête vers ProxyController sur A
3. ProxyController pipe le flux depuis B vers le client
4. Emby A peut transcoder à la volée si nécessaire

## Stack technique

- **Langage** : C# .NET 6+
- **Framework plugin** : Emby Plugin API (MediaBrowser.Common, MediaBrowser.Controller, MediaBrowser.Model)
- **HTTP Client** : HttpClient natif .NET
- **Serialisation** : System.Text.Json
- **Tests** : xUnit + Moq
- **CI** : GitHub Actions

## Structure du dépôt

```
/
├── CLAUDE.md                          # Ce fichier
├── README.md                          # Documentation utilisateur
├── docs/
│   ├── ARCHITECTURE.md                # Architecture détaillée
│   ├── API_EMBY.md                    # Référence API Emby utilisée
│   ├── CONNECTOR_SPEC.md              # Spec interface IMediaServerConnector
│   └── CONFIGURATION.md               # Guide configuration plugin
├── src/
│   └── Jellyfin.Plugin.VirtualLib/
│       ├── Plugin.cs
│       ├── PluginConfiguration.cs
│       ├── Core/
│       │   ├── IMediaServerConnector.cs
│       │   ├── Models/
│       │   ├── StrmGenerator.cs
│       │   ├── NfoGenerator.cs
│       │   └── LibrarySyncJob.cs
│       ├── Connectors/
│       │   ├── EmbyConnector.cs
│       │   ├── PlexConnector.cs       # Phase 3
│       │   └── JellyfinConnector.cs   # Phase 3
│       └── Api/
│           └── ProxyController.cs
└── tests/
    └── VirtualLib.Tests/
```

## Phases de développement

### Phase 1 — Core ✅ Terminé
- [x] Scaffolding projet C# plugin Emby
- [x] Interface `IMediaServerConnector` et modèles normalisés
- [x] `EmbyConnector` : auth, list libraries, list items, metadata
- [x] `StrmGenerator` : génération fichiers .strm
- [x] `NfoGenerator` : génération fichiers .nfo (movie + episode)
- [x] `PluginConfiguration` + `ConnectorConfig`
- [x] `Plugin.cs` entry point — plugin chargé et vérifié sur serveur de test
- [x] 18 tests unitaires (xUnit + Moq)

### Phase 1.5 — UI & Sync manuelle (priorité actuelle)
- [ ] Page de configuration HTML dans le dashboard Emby
- [ ] Formulaire ajout / édition / suppression d'un serveur source (URL + API key)
- [ ] Bouton "Tester la connexion" → `TestConnectionAsync`
- [ ] Listing des bibliothèques disponibles après connexion (`ListLibrariesAsync`)
- [ ] Sélection des bibliothèques à synchroniser (checkboxes → `ConnectorConfig.LibraryIds`)
- [ ] Bouton "Synchroniser maintenant" → génération `.strm` + `.nfo` + scan Emby
- [ ] Affichage progression / logs dans l'UI

### Phase 2a — Synchronisation des métadonnées
- [ ] `LibrarySyncJob` : tâche planifiée (`IScheduledTask`) configurable
- [ ] Détection delta : index JSON local `{connectorId}.json`
- [ ] Téléchargement artwork (poster, backdrop, thumb)
- [ ] Gestion des suppressions (items supprimés sur la source)
- [ ] Logs de sync dans le dashboard
- [ ] Tests intégration sync job

### Phase 2b — Proxy streaming
- [ ] `ProxyController` : endpoint `GET /virtuallib/proxy/{connectorId}/{itemId}`
- [ ] Support `Range` headers (seek / scrubbing)
- [ ] Mode proxy configurable à 3 niveaux :
  - Par serveur (`ConnectorConfig.ProxyMode`)
  - Par bibliothèque (override dans la config de lib)
  - Par média (override item-level)
- [ ] Modes : `Proxy` (passe par A) | `Direct` (URL source au client) | `Auto` (proxy si WAN, direct si LAN)
- [ ] Tests intégration ProxyController

### Phase 3 — Multi-connecteurs
- [ ] `JellyfinConnector` (API proche d'Emby)
- [ ] `PlexConnector` (API XML différente)
- [ ] Support N serveurs configurés simultanément
- [ ] UI multi-serveurs

## Règles de développement

### Conventions C#
- Nommage : PascalCase classes/méthodes, camelCase variables locales
- Interfaces préfixées `I` (ex: `IMediaServerConnector`)
- Async/await systématique pour tous les appels I/O
- `CancellationToken` propagé partout
- Pas de magic strings — constantes dans des classes dédiées

### Gestion des erreurs
- Logger Emby injecté via DI pour tous les logs
- Exceptions loggées + swallowed au niveau sync job (ne pas crasher Emby)
- Résultats d'opérations via `Result<T>` pattern ou exceptions typées
- Timeout configurable sur les appels HTTP vers serveurs distants

### Tests
- Un test par méthode publique minimum
- Mocks pour `HttpClient` (via `HttpMessageHandler` mockable)
- Pas de vrais appels réseau dans les tests unitaires
- Tests d'intégration séparés (répertoire `/tests/Integration/`)

### Git
- Branches : `feature/nom-feature`, `fix/nom-bug`
- Commits conventionnels : `feat:`, `fix:`, `test:`, `docs:`, `refactor:`
- PR obligatoire, pas de push direct sur `main`
- Chaque PR doit passer les tests CI

## Contexte Emby Plugin API

### Points d'entrée importants
```csharp
// Entry point du plugin
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages

// Scheduled task
public class LibrarySyncJob : IScheduledTask

// Controller HTTP custom
[Route("virtuallib")]
public class ProxyController : BaseApiController
```

### DI disponible dans les plugins Emby
- `ILibraryManager` — gestion bibliothèques
- `IFileSystem` — accès filesystem
- `IHttpClientFactory` — création HttpClient
- `ILogger<T>` — logging
- `IServerApplicationPaths` — chemins application

## Références

- [Emby Plugin API Docs](https://dev.emby.media/)
- [Emby Sample Plugins GitHub](https://github.com/MediaBrowser/Emby.Plugins)
- [Format NFO Emby](https://emby.media/support/articles/Movie-Naming.html)
- [Format STRM](https://emby.media/support/articles/strm-files.html)
- [Emby REST API](http://swagger.emby.media/)
