# Commande /test — Lancer les tests

## Usage
```
/test [filter]
```

Exemples :
```
/test                          # Tous les tests
/test EmbyConnector            # Tests d'un connecteur spécifique
/test ProxyController          # Tests du proxy
/test Core                     # Tests du core uniquement
```

## Commandes

```bash
# Tous les tests
dotnet test tests/VirtualLib.Tests/

# Avec filtre
dotnet test tests/VirtualLib.Tests/ --filter "FullyQualifiedName~{filter}"

# Avec couverture de code
dotnet test tests/VirtualLib.Tests/ \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage

# Rapport de couverture (si reportgenerator installé)
reportgenerator \
  -reports:coverage/**/coverage.cobertura.xml \
  -targetdir:coverage/report \
  -reporttypes:Html
```

## Seuils de couverture attendus

| Module | Couverture minimale |
|---|---|
| `Core/` | 80% |
| `Connectors/EmbyConnector` | 85% |
| `Api/ProxyController` | 75% |

## Structure des tests

```
tests/VirtualLib.Tests/
├── Core/
│   ├── LibrarySyncJobTests.cs
│   ├── StrmGeneratorTests.cs
│   ├── NfoGeneratorTests.cs
│   └── SyncIndexTests.cs
├── Connectors/
│   ├── EmbyConnectorTests.cs
│   └── ConnectorFactoryTests.cs
├── Api/
│   └── ProxyControllerTests.cs
└── Helpers/
    ├── MockHttpMessageHandler.cs   # Mock HttpClient réutilisable
    └── TestDataBuilder.cs          # Builders pour les modèles de test
```

## Pattern de mock HttpClient

```csharp
// Toujours utiliser MockHttpMessageHandler, jamais de vrais appels réseau
var handler = new MockHttpMessageHandler()
    .When("/emby/Library/VirtualFolders")
    .Respond(HttpStatusCode.OK, JsonContent.Create(fakeLibraries));

var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://emby-b:8096") };
var connector = new EmbyConnector(config, httpClient, logger);

var result = await connector.ListLibrariesAsync();
Assert.Equal(2, result.Count);
```
