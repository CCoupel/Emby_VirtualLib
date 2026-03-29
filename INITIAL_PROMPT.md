# Prompt de démarrage — Claude Code

## Contexte

Tu es en train de développer **VirtualLib**, un plugin C# pour Emby Server.

Ce plugin permet d'agréger des bibliothèques de serveurs médias distants (Emby, Jellyfin, Plex) comme bibliothèques virtuelles natives sur un serveur Emby hôte. Le serveur hôte sert de proxy transparent pour le streaming et le transcodage.

**Lire impérativement avant de commencer :**
- `CLAUDE.md` — vision, architecture, phases, règles de dev
- `docs/ARCHITECTURE.md` — composants détaillés et flux de données
- `docs/CONNECTOR_SPEC.md` — interface IMediaServerConnector et modèles
- `docs/API_EMBY.md` — référence des endpoints Emby utilisés

---

## Tâche initiale : Phase 1 MVP — Scaffolding

Créer la structure complète du projet C# et implémenter la Phase 1.

### Étapes dans l'ordre

**1. Projet C# — structure de base**

Créer la solution et les projets :
```
src/VirtualLib/VirtualLib.csproj          # Plugin principal
tests/VirtualLib.Tests/VirtualLib.Tests.csproj
VirtualLib.sln
```

Le `.csproj` du plugin doit :
- Cibler `net6.0`
- Référencer les packages Emby (MediaBrowser.Common, MediaBrowser.Controller, MediaBrowser.Model) en `PrivateAssets="all"`
- Référencer System.Text.Json

**2. Core — modèles et interface**

Créer dans `src/VirtualLib/Core/` :
- `Models/` — tous les modèles décrits dans `CONNECTOR_SPEC.md`
- `IMediaServerConnector.cs` — l'interface complète
- `ConnectorFactory.cs` — factory pattern

**3. EmbyConnector**

Implémenter `src/VirtualLib/Connectors/EmbyConnector.cs` :
- Auth via header `X-Emby-Token`
- `TestConnectionAsync` → `GET /emby/System/Info/Public`
- `ListLibrariesAsync` → `GET /emby/Library/VirtualFolders`
- `ListItemsAsync` → `GET /emby/Users/{userId}/Items` avec pagination complète
- `GetMetadataAsync` → `GET /emby/Items/{itemId}`
- `GetStreamUrlAsync` → construction statique de l'URL
- `GetArtworkStreamAsync` → `GET /emby/Items/{itemId}/Images/{type}`

Voir `docs/API_EMBY.md` pour les détails des endpoints et le mapping des champs.

**4. StrmGenerator**

Implémenter `src/VirtualLib/Core/StrmGenerator.cs` :
- Génère les `.strm` avec l'URL proxy : `http://localhost:8096/virtuallib/proxy/{connectorId}/{itemId}`
- Arborescence correcte pour films ET séries (voir ARCHITECTURE.md)
- Noms de fichiers safe (caractères spéciaux échappés)

**5. NfoGenerator**

Implémenter `src/VirtualLib/Core/NfoGenerator.cs` :
- Format `movie.nfo` pour les films
- Format `episodedetails.nfo` pour les épisodes
- Inclure : title, year, plot, rating, runtime, genres, studios, uniqueids (imdb/tmdb)

**6. PluginConfiguration + Plugin entry point**

- `src/VirtualLib/PluginConfiguration.cs` — modèle de config avec `List<ConnectorConfig>`
- `src/VirtualLib/Plugin.cs` — entry point Emby (`BasePlugin<PluginConfiguration>`)

**7. Tests unitaires**

Pour chaque composant créé, écrire les tests dans `tests/VirtualLib.Tests/` :
- `EmbyConnectorTests` — mocker HttpClient, tester mapping des réponses API
- `StrmGeneratorTests` — vérifier les URLs et arborescences générées
- `NfoGeneratorTests` — vérifier le XML produit

---

## Conventions à respecter

- Async/await + CancellationToken partout
- Logger injecté via DI (`ILogger<T>`)
- Pas de magic strings — constantes dans des classes dédiées
- Commits conventionnels : `feat:`, `fix:`, `test:`, `docs:`

## Commandes disponibles

- `/sync` — démarrer une synchronisation
- `/add-connector <Type>` — scaffolder un nouveau connecteur
- `/test [filtre]` — lancer les tests
- `/build [Release]` — compiler le plugin
