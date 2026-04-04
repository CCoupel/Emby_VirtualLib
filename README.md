# VirtualLib — Emby Plugin

Agrège les bibliothèques de serveurs médias distants comme bibliothèques virtuelles natives dans Emby.

## Fonctionnalités

- **Bibliothèques virtuelles** — films, séries, livres audio, ebooks et photos d'un serveur distant apparaissent comme des bibliothèques natives
- **Proxy transparent** — le streaming transite par le serveur hôte, permettant le transcodage
- **Sync automatique** — détection des ajouts sur les serveurs sources, sync incrémentale ou forcée
- **Métadonnées complètes** — titre, synopsis, artwork, cast, identifiants TMDB/IMDB sans re-scraping
- **Infos techniques** — résolution, codec, durée injectés sans re-probe (via `<fileinfo>` NFO)
- **États utilisateur synchronisés** — lu/non lu, favori, position de reprise propagés depuis le serveur source
- **Multi-sources** — Emby et Plex supportés simultanément
- **Progression temps réel** — 3 barres de progression dans le dashboard (connecteurs / bibliothèques / items)

## Serveurs supportés

| Serveur | Statut | Version |
|---|---|---|
| Emby | ✅ Supporté | v1.0.0+ |
| Plex | ✅ Supporté | v1.3.0+ |
| Jellyfin | 🔄 Planifié | — |

## Types de médias supportés

| Type | Statut |
|---|---|
| Films | ✅ |
| Séries / Épisodes | ✅ |
| Livres audio (AudioBook) | ✅ v1.4.0+ |
| Ebooks / PDF / ePub | ✅ v1.4.0+ |
| Photos / HomeVideos | ✅ v1.4.0+ |

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
