# PlexConnector

Implémentation de `IMediaServerConnector` pour les serveurs Plex.

---

## Authentification

Deux modes :

### Plex (IP directe)
- Header `X-Plex-Token: {token}` ajouté à chaque requête dès la construction
- Token obtenu depuis Settings → Account → Token sur le serveur Plex

### Plex via plex.tv
1. `POST https://plex.tv/users/sign_in.xml` avec credentials → `authToken` global
2. `GET https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1` → liste des serveurs
3. Sélection par `machineIdentifier` → `accessToken` propre au serveur (distinct du token global)
4. Résolution de la meilleure URL disponible parmi les connexions déclarées :
   - Priorité : IP locale → `plex.direct` → relay
   - Exclusion des IPs LAN inaccessibles depuis Kubernetes (RFC 1918 sur réseau différent)

> **Support 2FA** : code TOTP transmis au moment du chargement des serveurs via `POST /users/sign_in.xml?otp={code}`.

---

## Format des réponses

Plex retourne du **XML** (pas JSON). Éléments racine variés selon le type d'item :

| Type d'item | Élément XML | Attribut durée |
|---|---|---|
| Film / Épisode | `<Video>` | `duration` en **ms** → `× 10_000` → ticks |
| Show / Saison | `<Directory>` | n/a (conteneurs) |
| Piste audio | `<Track>` | `duration` en **ms** |

---

## États utilisateur

Attributs XML sur `<Video>` :

| Attribut XML | Type | Mapping `MediaItem` |
|---|---|---|
| `viewCount` | int | `PlayCount`, `IsPlayed = viewCount > 0` |
| `viewOffset` | ms (long) | `PlaybackPositionTicks = viewOffset × 10_000` |
| `lastViewedAt` | unix timestamp | `LastPlayedDate` |

Ces attributs sont parsés dans **deux méthodes** :
- `MapVideoToItem` — pour les items listés (existants)
- `MapVideoToMetadata` — pour les nouveaux items (path `GetMetadataAsync`)

> **Important** : si `MapVideoToMetadata` ne parse pas ces attributs, les états utilisateur seront perdus pour les items créés lors d'une nouvelle sync.

```csharp
var viewCount    = int.TryParse(video.Attribute("viewCount")?.Value, out var vc) ? vc : 0;
var viewOffsetMs = long.TryParse(video.Attribute("viewOffset")?.Value, out var vo) ? vo : 0L;
var lastViewedAt = ParseUnixTimestamp(video, "lastViewedAt");

// Dans MediaItem :
IsPlayed              = viewCount > 0,
PlayCount             = viewCount,
LastPlayedDate        = lastViewedAt,
PlaybackPositionTicks = viewOffsetMs > 0 ? viewOffsetMs * 10_000L : 0
```

---

## TechnicalInfo depuis Plex

Parsé depuis l'élément `<Media>` enfant du `<Video>` :

| Attribut XML | Champ `TechnicalInfo` | Conversion |
|---|---|---|
| `bitrate` | `Bitrate` | kbps → `× 1000` bps |
| `videoCodec` | `VideoCodec` | direct |
| `audioCodec` | `AudioCodec` | direct |
| `width` | `Width` | direct |
| `height` | `Height` | direct |
| `audioChannels` | `AudioChannels` | direct |
| `container` | `Container` | direct |

> Plex envoie `bitrate` en **kbps** — multiplier par 1000 pour obtenir des bps (unité attendue par `TechnicalInfo`).

---

## Reporting de lecture (PlaybackEventForwarder)

Plex utilise deux endpoints pour le suivi de lecture :

| Endpoint | Rôle |
|---|---|
| `GET /:/timeline` | Met à jour le statut "Now Playing" (dashboard Plex, sessions actives) |
| `GET /:/progress` | **Persiste le `viewOffset`** en base (barre de progression, "Continuer la lecture") |

> **Important** : `/:/timeline` seul ne persiste pas le `viewOffset`. Il faut appeler `/:/progress` pour que la position soit sauvegardée côté Plex.

### Paramètres `/:/timeline`

```
GET /:/timeline?hasMDE=1&ratingKey={itemId}&key=/library/metadata/{itemId}&state={playing|paused|stopped}&time={positionMs}&X-Plex-Token={token}
Header: X-Plex-Session-Identifier: {playSessionId}
```

### Paramètres `/:/progress`

```
GET /:/progress?key={ratingKey}&identifier=com.plexapp.plugins.library&time={positionMs}&state={stopped|paused}&X-Plex-Token={token}
```

> **Piège** : `key` dans `/:/progress` doit être le **ratingKey numérique** (`31004`), PAS le chemin complet (`/library/metadata/31004`) — qui renvoie 500.

### Conversion ticks → ms

```csharp
positionMs = positionTicks / 10_000L;   // 1 tick = 100 ns, 1 ms = 10 000 ticks
```

### Appels par état

| État | `/:/timeline` | `/:/progress` |
|---|---|---|
| Start | `state=playing&time=0` | — |
| Progress (playing) | `state=playing&time={ms}` | — |
| Progress (paused) | `state=paused&time={ms}` | `state=paused&time={ms}` |
| Stop | `state=stopped&time={ms}` | `state=stopped&time={ms}` |

---

## Pièges et points d'attention

| Piège | Description |
|---|---|
| `duration` en ms | Emby attend des ticks (100 ns). Conversion : `ms × 10_000`. |
| Show/Season → `<Directory>` | `GetMetadataAsync` pour un Show ou Season retourne un `<Directory>`, pas un `<Video>`. `MapDirectoryToMetadata` gère ce cas. Chercher uniquement `<Video>` → exception silencieuse. |
| `parentRatingKey` | Sur un épisode = `SeasonId`. Si absent → le bloc artwork/NFO de saison ne s'exécute jamais. |
| Relay Plex | Latence élevée (30 s+). Timeout configuré à 120 s. |
| `X-Plex-Container-Start=0` | Requis pour obtenir `totalSize` dans la réponse paginée. |
| `accessToken` par serveur | Distinct du token global plex.tv — obtenu depuis `/api/v2/resources`. |
