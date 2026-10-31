# dev-plugin — Adaptations VirtualLib (Emby Plugin C#/.NET 6)

> Compagnon de `.claude/agents/dev-plugin.template.md`. Le template est generique (exemples
> TypeScript/npm, `activate()/deactivate()`, `manifest.json`) : pour VirtualLib, les sections
> ci-dessous **remplacent** ses sections Expertise, Structure, Conventions, Commandes et Checklist.
> Le protocole teammate (IDLE, rapports TERMINE/DIAGNOSTIC) reste celui du template.

## Regles projet

- Respecter l'architecture existante (`IMediaServerConnector`, `StrmGenerator`, `NfoGenerator`, `LibrarySyncJob`, `ProxyController`) avant d'ajouter du code
- Contrats de l'API hote et des serveurs distants : `docs/API_EMBY.md` et `docs/CONNECTOR_SPEC.md` (en plus de `contracts/`) — ne jamais appeler une API Emby/Plex/Jellyfin non documentee
- Scope de commit `test`/`docs` autorise en plus de `feat/fix/refactor`

## Expertise

- Cycle de vie plugin Emby (`BasePlugin<PluginConfiguration>`, `IHasWebPages`, activation au demarrage serveur)
- Scheduled tasks (`IScheduledTask` — `LibrarySyncJob`)
- Controllers HTTP custom (`BaseApiController`, route `[Route("virtuallib")]` — `ProxyController`, `ConfigController`)
- Connecteurs serveurs distants (`IMediaServerConnector` — `EmbyConnector`, `PlexConnector`, `PlexTvConnector`)
- Generation `.strm` / `.nfo` (`StrmGenerator`, `NfoGenerator`, `EpubStubGenerator`)
- Cache et manifestes de chunks (`CacheManager`, `ChunkManifest`)
- Async/await systematique + propagation `CancellationToken` sur tout appel I/O
- DI Emby disponible : `ILibraryManager`, `IFileSystem`, `IHttpClientFactory`, `ILogger<T>`, `IServerApplicationPaths`

## Structure Projet

```
src/VirtualLib/
├── Plugin.cs                     # Entry point (BasePlugin<PluginConfiguration>, IHasWebPages)
├── PluginConfiguration.cs
├── Core/
│   ├── IMediaServerConnector.cs
│   ├── ConnectorFactory.cs
│   ├── Models/
│   ├── Cache/                    # CacheManager, ChunkManifest, ICacheManager
│   ├── StrmGenerator.cs
│   ├── NfoGenerator.cs
│   ├── EpubStubGenerator.cs
│   ├── LibraryProvisioner.cs
│   ├── LibrarySyncJob.cs         # IScheduledTask
│   ├── SyncService.cs
│   ├── SyncState.cs
│   └── PlaybackEventForwarder.cs
├── Connectors/
│   ├── EmbyConnector.cs
│   ├── PlexConnector.cs
│   ├── PlexTvConnector.cs
│   └── Internal/                 # Modeles API prives (EmbyApiModels, PlexTvApiModels)
├── Providers/                    # NfoProviders (Book, AudioBook, AudioBookFolder) + NfoXmlReader
├── Api/
│   ├── ProxyController.cs
│   └── ConfigController.cs
└── Web_Pages/                    # config.html, configjs.js (page config embarquee)

tests/VirtualLib.Tests/
├── Core/
├── Connectors/
├── Api/
└── Helpers/                      # MockHttpMessageHandler, TestDataBuilder
```

## Conventions

### Ajouter un connecteur

Voir `docs/CONNECTOR_SPEC.md` — implementer `IMediaServerConnector`, l'enregistrer dans `ConnectorFactory`,
ajouter les modeles prives dans `Connectors/Internal/`. Un connecteur ne doit jamais fuiter ses modeles
internes hors de son propre fichier — exposer uniquement les types de `Core/Models/`.

### Scheduled Task

```csharp
public class LibrarySyncJob : IScheduledTask
{
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        // toute I/O propage cancellationToken
    }
}
```

### Controller HTTP custom

```csharp
[Route("virtuallib/proxy/{connectorId}/{itemId}", "GET")]
public class ProxyStreamRequest : IReturn<object> { }

public class ProxyController : BaseApiController
{
    // pipe le flux depuis le serveur source vers le client, timeout configurable
}
```

### Gestion d'Erreurs

```csharp
// Logger injecte via DI, exceptions loggees + swallowed au niveau sync job
try
{
    await connector.SyncAsync(cancellationToken);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Sync failed for connector {ConnectorId}", connector.Id);
    // ne jamais laisser une exception crasher Emby
}
```

### Tests — Mock HttpClient

```csharp
// Toujours utiliser MockHttpMessageHandler, jamais de vrais appels reseau
var handler = new MockHttpMessageHandler()
    .When("/emby/Library/VirtualFolders")
    .Respond(HttpStatusCode.OK, JsonContent.Create(fakeLibraries));

var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://emby-b:8096") };
var connector = new EmbyConnector(config, httpClient, logger);

var result = await connector.ListLibrariesAsync();
Assert.Equal(2, result.Count);
```

## Commandes

```bash
# Build
dotnet build src/VirtualLib --configuration Release --output dist

# Tests
dotnet test tests/VirtualLib.Tests/

# Tests avec filtre
dotnet test tests/VirtualLib.Tests/ --filter "FullyQualifiedName~{filter}"

# CI complete (build + tests + audit) — identique a la CI GitHub
bash scripts/ci.sh

# Audit vulnerabilites
dotnet list src/VirtualLib package --vulnerable
```

## Checklist Implementation

- [ ] Respecte l'interface `IMediaServerConnector` sans fuiter de modeles internes
- [ ] `CancellationToken` propage sur tout appel I/O
- [ ] Exceptions loggees via `ILogger<T>` injecte, jamais non gerees jusqu'a Emby
- [ ] Pas de magic strings — constantes dans une classe dediee
- [ ] Timeout configurable sur les appels HTTP vers serveurs distants
- [ ] Tests unitaires avec `MockHttpMessageHandler` (pas de vrais appels reseau)
- [ ] Couverture : `Core/` 80%, `Connectors/EmbyConnector` 85%, `Api/ProxyController` 75%
- [ ] Commit conventionnel (`feat/fix/refactor/test/docs(scope): ...`)
