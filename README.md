# VirtualLib — Emby Plugin

Agrège les bibliothèques de serveurs médias distants comme bibliothèques virtuelles natives dans Emby.

## Fonctionnalités

- **Bibliothèques virtuelles** — films, séries, livres audio, ebooks et photos d'un serveur distant apparaissent comme des bibliothèques natives
- **Proxy transparent** — le streaming transite par le serveur hôte, permettant le transcodage
- **Cache local** — les médias sont mis en cache par segments alignés sur les chunks ; les clients concurrents partagent le même téléchargement en cours, sans double-fetch de la source ; si le média dépasse le seuil de complétion configuré (défaut 90 %), le téléchargement se poursuit automatiquement jusqu'à la fin même si le client s'arrête
- **Sync automatique** — détection des ajouts sur les serveurs sources, sync incrémentale ou forcée
- **Métadonnées complètes** — titre, synopsis, artwork, cast, identifiants TMDB/IMDB sans re-scraping
- **Infos techniques** — résolution, codec, durée injectés sans re-probe (via `<fileinfo>` NFO)
- **États utilisateur synchronisés** — lu/non lu, favori, position de reprise propagés depuis le serveur source
- **Backpropagation lecture** — start/progress/stop/position remontés en temps réel vers le serveur distant pendant la lecture (Emby + Plex)
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

## Documentation

| Document | Contenu |
|---|---|
| [docs/core/OVERVIEW.md](docs/core/OVERVIEW.md) | Architecture, flux, sécurité |
| [docs/core/SYNC.md](docs/core/SYNC.md) | Algorithme de sync, NFO, états utilisateur |
| [docs/core/PROXY.md](docs/core/PROXY.md) | ProxyController, Range, pièges |
| [docs/core/CACHE.md](docs/core/CACHE.md) | Cache local — segments, chunks, pending, seuil de complétion |
| [docs/core/API_EMBY.md](docs/core/API_EMBY.md) | Référence endpoints Emby REST |
| [docs/connectors/SPEC.md](docs/connectors/SPEC.md) | Interface IMediaServerConnector, modèles |
| [docs/connectors/EMBY.md](docs/connectors/EMBY.md) | EmbyConnector |
| [docs/connectors/PLEX.md](docs/connectors/PLEX.md) | PlexConnector |
| [docs/media-types/VIDEO.md](docs/media-types/VIDEO.md) | Films, séries — NFO, arborescences |
| [docs/media-types/AUDIOBOOK_BOOK.md](docs/media-types/AUDIOBOOK_BOOK.md) | Livres audio, ebooks |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | Guide utilisateur |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Roadmap et backlog |

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
