# Référence API Emby — VirtualLib

Documentation des endpoints Emby utilisés par le plugin. Basé sur l'API Emby 4.x (compatible Jellyfin).

## Authentification

Toutes les requêtes nécessitent un header d'authentification :

```
X-Emby-Token: {api_key}
```

Ou pour la compatibilité Jellyfin :
```
X-MediaBrowser-Token: {api_key}
```

**Obtenir l'API key** : Dashboard Emby → Avancé → Clés API

**Obtenir l'userId** (nécessaire pour certains endpoints) :
```http
GET /emby/Users/Me
X-Emby-Token: {api_key}
```
```json
{
  "Id": "a1b2c3d4e5f6...",
  "Name": "admin"
}
```

---

## Endpoints utilisés

### Lister les bibliothèques

```http
GET /emby/Library/VirtualFolders
X-Emby-Token: {api_key}
```

**Réponse** :
```json
[
  {
    "Name": "Films",
    "ItemId": "abc123",
    "CollectionType": "movies",
    "Locations": ["/media/films"]
  },
  {
    "Name": "Séries",
    "ItemId": "def456",
    "CollectionType": "tvshows",
    "Locations": ["/media/series"]
  }
]
```

**Mapping vers `RemoteLibrary`** :
- `ItemId` → `RemoteLibrary.Id`
- `Name` → `RemoteLibrary.Name`
- `CollectionType` → `RemoteLibrary.Type` (`"movies"` → `LibraryType.Movies`)

---

### Lister les items d'une bibliothèque

```http
GET /emby/Users/{userId}/Items
    ?ParentId={libraryId}
    &Recursive=true
    &IncludeItemTypes=Movie,Episode
    &Fields=Overview,Genres,Studios,ProviderIds,DateCreated
    &StartIndex=0
    &Limit=100
X-Emby-Token: {api_key}
```

**Réponse** :
```json
{
  "Items": [
    {
      "Id": "12345",
      "Name": "Inception",
      "Type": "Movie",
      "ProductionYear": 2010,
      "Overview": "Un voleur...",
      "Genres": ["Science-Fiction", "Thriller"],
      "Studios": [{"Name": "Warner Bros."}],
      "ProviderIds": {
        "Imdb": "tt1375666",
        "Tmdb": "27205"
      },
      "DateCreated": "2023-01-15T10:00:00Z",
      "RunTimeTicks": 8880000000,
      "CommunityRating": 8.8,
      "SeriesName": null,
      "ParentIndexNumber": null,
      "IndexNumber": null
    },
    {
      "Id": "67890",
      "Name": "Pilot",
      "Type": "Episode",
      "SeriesName": "Breaking Bad",
      "ParentIndexNumber": 1,
      "IndexNumber": 1,
      "ProductionYear": 2008
    }
  ],
  "TotalRecordCount": 342,
  "StartIndex": 0
}
```

**Notes de pagination** :
- Itérer avec `StartIndex` jusqu'à `StartIndex >= TotalRecordCount`
- `Limit` recommandé : 100-500 selon les performances du serveur source

**Mapping vers `MediaItem`** :
- `Id` → `RemoteId`
- `Type == "Movie"` → `MediaType.Movie`
- `Type == "Episode"` → `MediaType.Episode`
- `RunTimeTicks / 600000000` → `RuntimeMinutes` (ticks = 100ns)
- `ParentIndexNumber` → `SeasonNumber`
- `IndexNumber` → `EpisodeNumber`

---

### Métadonnées complètes d'un item

```http
GET /emby/Items/{itemId}
    ?Fields=Overview,Genres,Studios,ProviderIds,People,Tags
X-Emby-Token: {api_key}
```

Même structure que ci-dessus mais pour un seul item, avec champs supplémentaires disponibles.

---

### Artwork

```http
GET /emby/Items/{itemId}/Images/{imageType}
    ?Quality=90
    &MaxWidth=400
X-Emby-Token: {api_key}
```

**Types d'images** :
| `imageType` | Usage | Fichier local généré |
|---|---|---|
| `Primary` | Poster | `poster.jpg` |
| `Backdrop` | Fond d'écran | `fanart.jpg` |
| `Thumb` | Miniature épisode | `thumb.jpg` |
| `Logo` | Logo série | `clearlogo.png` |

**Réponse** : binaire image (JPEG ou PNG selon la source)

**Vérifier si une image existe** : header `X-Image-Info` dans la réponse, ou code 404 si absente.

---

### URL de stream

L'URL de stream est construite statiquement (pas d'appel API nécessaire) :

```
http://{serverUrl}/Videos/{itemId}/stream?api_key={api_key}&Static=true
```

Paramètres utiles :
- `Static=true` — force le direct play sans transcoding côté source
- `MediaSourceId={itemId}` — spécifie la source média (utile si multi-version)
- `AudioStreamIndex=1` — piste audio spécifique
- `SubtitleStreamIndex=-1` — désactive les sous-titres

**URL pour le .strm (via proxy local)** :
```
http://localhost:8096/virtuallib/proxy/{connectorId}/{itemId}
```

---

### Déclencher un scan de bibliothèque

Après génération des .strm, déclencher le scan via l'API Emby locale (pas distante) :

```http
POST /emby/Library/Refresh
X-Emby-Token: {local_api_key}
```

Ou scan d'une bibliothèque spécifique :
```http
POST /emby/Items/{libraryId}/Refresh
X-Emby-Token: {local_api_key}
```

---

## Gestion des erreurs HTTP

| Code | Signification | Comportement recommandé |
|---|---|---|
| 200 | OK | Traiter la réponse |
| 206 | Partial Content | Normal pour les streams avec Range |
| 401 | API key invalide | Logger erreur, marquer connector KO |
| 404 | Item non trouvé | Logger warning, skip l'item |
| 429 | Rate limit | Retry avec backoff exponentiel |
| 500 | Erreur serveur | Logger erreur, retry après délai |
| Timeout | Serveur injoignable | Logger warning, skip la sync |

---

## Différences Emby vs Jellyfin

L'API Jellyfin est très proche d'Emby (fork) mais avec quelques différences :

| Aspect | Emby | Jellyfin |
|---|---|---|
| Header auth | `X-Emby-Token` | `X-MediaBrowser-Token` |
| Base path | `/emby/` | `/` |
| ProviderIds | Identique | Identique |
| Stream URL | `/Videos/{id}/stream` | `/Videos/{id}/stream` |

Le `JellyfinConnector` sera quasi identique à `EmbyConnector` avec ces ajustements mineurs.
