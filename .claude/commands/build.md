# Commande /build — Compiler le plugin

## Usage
```
/build [configuration]
```

Exemples :
```
/build           # Debug par défaut
/build Release   # Build de release
```

## Commandes

```bash
# Restore des dépendances
dotnet restore src/VirtualLib/

# Build Debug
dotnet build src/VirtualLib/ --configuration Debug

# Build Release (pour distribution)
dotnet build src/VirtualLib/ \
  --configuration Release \
  --output ./dist/

# Créer le package plugin Emby (.zip)
cd dist/
zip -r VirtualLib_$(cat ../src/VirtualLib/VirtualLib.csproj | grep '<Version>' | sed 's/.*>\(.*\)<.*/\1/').zip \
  VirtualLib.dll \
  meta.json
```

## Fichier meta.json requis par Emby

```json
{
  "category": "General",
  "description": "Aggregate media libraries from remote Emby/Jellyfin/Plex servers as virtual libraries",
  "guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "VirtualLib",
  "overview": "Stream and browse media from remote servers through a unified virtual library interface",
  "owner": "VirtualLib",
  "targetAbi": "4.8.0.0",
  "timestamp": "",
  "version": "1.0.0"
}
```

## Vérifications post-build

- [ ] `VirtualLib.dll` présent dans `dist/`
- [ ] Pas de warnings de compilation
- [ ] Version dans `meta.json` correspond à `VirtualLib.csproj`
- [ ] Tests passent (`/test`)

## Dépendances NuGet

```xml
<!-- Dans VirtualLib.csproj -->
<PackageReference Include="Emby.Naming" Version="4.*" />
<PackageReference Include="MediaBrowser.Common" Version="4.*" />
<PackageReference Include="MediaBrowser.Controller" Version="4.*" />
<PackageReference Include="MediaBrowser.Model" Version="4.*" />
```

Ces packages sont fournis par l'environnement Emby à l'exécution.
Ils doivent être référencés avec `PrivateAssets="all"` pour ne pas être inclus dans le .zip.
