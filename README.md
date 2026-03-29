# VirtualLib — Emby Plugin

Agrège les bibliothèques de serveurs médias distants comme bibliothèques virtuelles natives dans Emby.

## Fonctionnalités

- **Bibliothèques virtuelles** — les médias d'un serveur Emby/Jellyfin distant apparaissent comme des bibliothèques natives
- **Proxy transparent** — le streaming transite par le serveur hôte, permettant le transcodage
- **Sync automatique** — détection des ajouts/suppressions sur les serveurs sources
- **Métadonnées complètes** — titre, synopsis, artwork, identifiants TMDB/IMDB sans re-scraping
- **Multi-sources** — support de plusieurs serveurs simultanément

## Serveurs supportés

| Serveur | Statut |
|---|---|
| Emby | ✅ Phase 1 |
| Jellyfin | 🔄 Phase 3 |
| Plex | 🔄 Phase 3 |

## Installation

Voir [docs/CONFIGURATION.md](docs/CONFIGURATION.md) pour le guide d'installation complet.

## Architecture

Voir [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) pour la documentation technique.

## Développement

```bash
# Cloner le dépôt
git clone https://github.com/xxx/virtuallib-emby-plugin
cd virtuallib-emby-plugin

# Builder
dotnet build src/VirtualLib/

# Tests
dotnet test tests/VirtualLib.Tests/
```

## Contribuer

1. Fork le dépôt
2. Créer une branche `feature/ma-feature`
3. Commiter avec les conventions : `feat:`, `fix:`, `test:`, `docs:`
4. Ouvrir une PR

## Licence

MIT
