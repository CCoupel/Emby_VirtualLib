# Changelog des contrats

> Tout changement BREAKING doit être signalé explicitement. Le CDP lit ce fichier après la phase PLAN pour alerter l'utilisateur au GATE 2.

---

## [20260804] — Filtrage des médias distants (#44, milestone v1.10.0)

**Aucun changement BREAKING.** Tous les ajouts sont additifs ; une configuration existante sans règle produit exactement le comportement antérieur.

### Modèles

- **[NEW]** `MediaStreamKind` — enum `Video | Audio | Subtitle | Other`
- **[NEW]** `MediaStreamInfo` — une piste média (Kind, Index, Codec, Language, LanguageCode, IsDefault, IsForced, IsExternal, IsHearingImpaired, Width, Height, Channels)
- **[NEW]** `MediaFilterConfig` — règles de filtrage par connecteur (MinHeight, AudioLanguages, SubtitleLanguages, IgnoreForcedSubtitles)
- **[CHANGED]** `TechnicalInfo` — ajout de `Streams` (liste vide par défaut). Les neuf champs scalaires existants sont **inchangés** et restent alimentés à l'identique. Rétrocompatible.
- **[CHANGED]** `ConnectorConfig` — ajout de `MediaFilter`
- **[CHANGED]** `LibrarySyncResult` — ajout de `ItemsFiltered`

### Interfaces

- **[CHANGED]** `IMediaServerConnector` — ajout de `GetStreamInfoAsync(IReadOnlyList<string>, CancellationToken)`.
  ⚠️ Impact interne : les trois implémentations (`EmbyConnector`, `PlexConnector`, `PlexTvConnector`) doivent la fournir. Interface non publique hors du plugin — **pas un breaking change** pour les consommateurs externes.
- **[NEW]** `ILibraryCleanupService` — détection et suppression des orphelins, brique partagée avec #12

### Endpoints HTTP

- **[CHANGED]** `POST /virtuallib/connectors` — 4 champs ajoutés : `FilterMinHeight`, `FilterAudioLanguages`, `FilterSubtitleLanguages`, `FilterIgnoreForcedSubtitles`. Tous optionnels, valeurs par défaut neutres.
- **[CHANGED]** `PUT /virtuallib/connectors/{Id}` — mêmes 4 champs
- **[CHANGED]** `GET /virtuallib/settings` · `PUT /virtuallib/settings` — ajout de `OrphanCleanupEnabled` (défaut `false`)
- **[CHANGED]** endpoints de synchronisation — `ItemsFiltered` dans les résultats par bibliothèque

### Correctifs de contrat adjacents *(à confirmer par le CDP — hors périmètre littéral de #44)*

- **[CHANGED]** `POST`/`PUT /virtuallib/connectors` — ajout de `CacheEnabled` aux DTOs.
  Bug préexistant : le champ existe sur `ConnectorConfig` et l'UI l'envoie, mais il est absent des DTOs et des initialiseurs — décocher « Enable cache for this connector » n'a jamais été persisté.
- **[CHANGED]** payload `toggleLibrary` (`configjs.js`) — ajout de `LocalUserId` et `CacheEnabled`.
  Bug préexistant : basculer une checkbox de bibliothèque efface `LocalUserId` côté serveur. Sans ce correctif, le même geste effacerait aussi les règles de filtrage.
