# Commande /add-connector — Ajouter un nouveau connecteur

## Usage
```
/add-connector <ServerType>
```

Exemple :
```
/add-connector Plex
/add-connector Jellyfin
```

## Description
Scaffolde un nouveau connecteur pour un type de serveur média. Crée les fichiers nécessaires et les stubs à implémenter.

## Fichiers à créer

### 1. `src/VirtualLib/Connectors/{ServerType}Connector.cs`

```csharp
namespace VirtualLib.Connectors
{
    public class {ServerType}Connector : IMediaServerConnector
    {
        public string ServerType => "{ServerType}";
        // ... implémenter IMediaServerConnector
    }
}
```

### 2. `tests/VirtualLib.Tests/Connectors/{ServerType}ConnectorTests.cs`

Tests unitaires avec mocks HttpClient pour :
- `TestConnectionAsync` — succès et échec
- `ListLibrariesAsync` — mapping correct vers `RemoteLibrary`
- `ListItemsAsync` — pagination, mapping Movie/Episode
- `GetMetadataAsync` — tous les champs
- `GetStreamUrlAsync` — format URL correct

### 3. Mettre à jour `src/VirtualLib/Core/ConnectorFactory.cs`

Ajouter le case dans le switch :
```csharp
"{ServerType}" => new {ServerType}Connector(config, _httpClientFactory, ...),
```

### 4. Mettre à jour `docs/API_{SERVERTYPE}.md`

Documenter les endpoints API du nouveau serveur utilisés.

## Checklist d'implémentation

- [ ] `TestConnectionAsync` — endpoint de santé du serveur
- [ ] `ListLibrariesAsync` — liste des bibliothèques
- [ ] `ListItemsAsync` — avec pagination complète
- [ ] `GetMetadataAsync` — tous les champs de `MediaMetadata`
- [ ] `GetStreamUrlAsync` — URL avec auth
- [ ] `GetArtworkStreamAsync` — Poster + Backdrop minimum
- [ ] Tests unitaires pour chaque méthode publique
- [ ] Documentation API dans `docs/`
