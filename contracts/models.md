# Contrats — Modèles de données

> Référence en cas de divergence entre couches. Le développement PEUT modifier un contrat sous contrainte technique, à condition de documenter la raison ici et dans `CHANGELOG.md`.

---

## `MediaStreamKind` *(nouveau — #44)*

Enum. Nature d'une piste média.

| Valeur | Origine Emby (`MediaStream.Type`) | Origine Plex (`Stream@streamType`) |
|---|---|---|
| `Video` | `Video` | `1` |
| `Audio` | `Audio` | `2` |
| `Subtitle` | `Subtitle` | `3` |
| `Other` | tout autre (`EmbeddedImage`, `Attachment`, `Data`, `Unknown`) | tout autre |

---

## `MediaStreamInfo` *(nouveau — #44)*

Une piste d'un média distant. Classe scellée, propriétés `init`.

| Champ | Type | Description |
|---|---|---|
| `Kind` | `MediaStreamKind` | Nature de la piste |
| `Index` | `int?` | Index de la piste dans le conteneur |
| `Codec` | `string?` | Codec (`h264`, `ac3`, `srt`…) |
| `Language` | `string?` | Libellé brut tel que reçu du serveur source (`fre`, `French`, `Français`) |
| `LanguageCode` | `string?` | Code canonique 3 lettres minuscules, normalisé par `LanguageMatcher`. `null` si indéterminable. |
| `IsDefault` | `bool` | Piste par défaut |
| `IsForced` | `bool` | Sous-titre forcé |
| `IsExternal` | `bool` | Piste externe (fichier `.srt` adjacent) |
| `IsHearingImpaired` | `bool` | Piste pour malentendants |
| `Width` | `int?` | Vidéo uniquement |
| `Height` | `int?` | Vidéo uniquement |
| `Channels` | `int?` | Audio uniquement |

**Règle de normalisation `LanguageCode`** — `fr`, `fre`, `fra`, `french`, `français` convergent tous vers `fra`. Un code non reconnu est conservé en minuscules sans exception levée.

---

## `TechnicalInfo` *(étendu — #44)*

**Ajout additif. Les neuf champs scalaires existants sont inchangés** (`Size`, `Bitrate`, `Container`, `Width`, `Height`, `VideoCodec`, `AudioCodec`, `AudioChannels`, `AudioSampleRate`) et restent alimentés depuis la première piste vidéo et la première piste audio.

| Champ ajouté | Type | Défaut |
|---|---|---|
| `Streams` | `IReadOnlyList<MediaStreamInfo>` | liste vide |

**Invariant** : `Streams` vide ⇒ tout consommateur retombe sur le comportement scalaire actuel. Aucune régression possible sur `NfoGenerator` et `SaveMediaStreams`.

---

## `MediaFilterConfig` *(nouveau — #44)*

Règles de filtrage, portées par `ConnectorConfig.MediaFilter`.

| Champ | Type | Valeur neutre | Sémantique |
|---|---|---|---|
| `MinHeight` | `int` | `0` | Hauteur vidéo minimale en pixels (`720`, `1080`, `2160`) |
| `AudioLanguages` | `List<string>` | vide | Codes acceptés — **OU** entre eux |
| `SubtitleLanguages` | `List<string>` | vide | Codes acceptés — **OU** entre eux |
| `IgnoreForcedSubtitles` | `bool` | `false` | Exclut les pistes `IsForced` du test sous-titres |

**Propriété calculée** : `IsActive` ⇔ au moins un critère non neutre.

**Sémantique d'ensemble**
- **ET** entre critères de nature différente, **OU** à l'intérieur d'un critère.
- `IsActive == false` ⇒ aucune évaluation, tous les items sont conservés (comportement antérieur strictement préservé).
- Portée : types `Movie` et `Episode` uniquement. Tout autre `MediaType` est conservé sans évaluation.
- **Fail-open** : un item dont l'information est indisponible est conservé, avec journalisation agrégée.

---

## `LibrarySyncResult` *(étendu — #44)*

| Champ ajouté | Type | Description |
|---|---|---|
| `ItemsFiltered` | `int` | Items écartés par les règles de filtrage |

`ItemsCreated`, `ItemsSkipped`, `ItemsFailed` conservent leur sémantique.

---

## `IMediaServerConnector` *(étendu — #44)*

```csharp
Task<IReadOnlyDictionary<string, IReadOnlyList<MediaStreamInfo>>> GetStreamInfoAsync(
    IReadOnlyList<string> remoteIds,
    CancellationToken cancellationToken = default);
```

Récupère les pistes des items indiqués, lorsqu'elles ne sont pas déjà présentes sur `MediaItem.Technical.Streams`.

| Implémentation | Comportement |
|---|---|
| `EmbyConnector` | Retourne un dictionnaire **vide** — les pistes sont déjà fournies par `ListItemsAsync`. Aucun appel réseau. |
| `PlexConnector` | `GET library/metadata/{id1,…,idN}` par lots de 50, dégradation récursive par moitié sur `4xx`, 4 lots en parallèle maximum. |
| `PlexTvConnector` | Délègue au `PlexConnector` interne. |

**Contrat d'appel** : le `SyncService` n'invoque cette méthode que si `MediaFilter.IsActive` **et** qu'au moins une règle porte sur la langue ou les sous-titres. Une règle de résolution seule ne déclenche aucun appel.

**Contrat de retour** : une clé absente du dictionnaire signifie « information indisponible » et déclenche le fail-open — elle ne signifie pas « aucune piste ».
