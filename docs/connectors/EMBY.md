# EmbyConnector

Implémentation de `IMediaServerConnector` pour les serveurs Emby (et Jellyfin dont l'API est compatible).

Référence des endpoints REST utilisés → [core/API_EMBY.md](../core/API_EMBY.md).

---

## Authentification

Deux modes supportés :

| Mode | Header | Usage |
|---|---|---|
| **API Key** | `X-Emby-Token: {key}` | Accès admin — les états utilisateur retournés sont ceux de l'admin (souvent à zéro) |
| **User Credentials** | Authentification via `/Users/AuthenticateByName` | **Recommandé** — états utilisateur réels de l'utilisateur connecté |

> Pour que la synchronisation des états lu/favori/position soit correcte, utiliser **User Credentials**.

---

## Pattern d'URL — règle critique

`ConnectorConfig.ServerUrl` doit inclure le chemin de base complet.
`HttpClient.BaseAddress` est défini sur `{ServerUrl}/` (avec slash final).
Les chemins relatifs dans le connector ne doivent **pas** répéter ce préfixe :

```csharp
// ✅ Correct — BaseAddress = "https://media.example.com/emby/"
"Library/VirtualFolders"
$"Users/{userId}/Items?..."

// ❌ Incorrect — double préfixe /emby/emby/
"emby/Library/VirtualFolders"
```

---

## Endpoints utilisés

```
GET Library/VirtualFolders
GET Users/{userId}/Items?ParentId={id}&Fields=...,MediaSources,UserData
GET Users/{userId}/Items/{itemId}?Fields=...,MediaSources,UserData
GET Items/{itemId}/Images/{type}
GET {ServerUrl}/Videos/{itemId}/stream   (URL absolue)
```

---

## Gestion de la pagination

```csharp
while (startIndex < totalCount)
{
    var page = await GetItemsPageAsync(libraryId, startIndex, pageSize, ct);
    items.AddRange(page.Items);
    startIndex += pageSize;
    totalCount  = page.TotalRecordCount;
}
```

---

## UserData — états utilisateur

Le champ `UserData` est inclus dans le paramètre `Fields` des requêtes list et metadata :

```
&Fields=Overview,Genres,Studios,ProviderIds,DateCreated,Tags,Album,AlbumId,MediaSources,UserData
```

Mapping `EmbyUserData` → `MediaItem` :

```csharp
IsPlayed              = item.UserData?.Played                ?? false,
IsFavorite            = item.UserData?.IsFavorite            ?? false,
PlayCount             = item.UserData?.PlayCount             ?? 0,
LastPlayedDate        = item.UserData?.LastPlayedDate,
PlaybackPositionTicks = item.UserData?.PlaybackPositionTicks ?? 0
```

Modèle JSON côté Emby :
```json
"UserData": {
  "Played": true,
  "IsFavorite": false,
  "PlayCount": 2,
  "LastPlayedDate": "2026-03-15T20:00:00Z",
  "PlaybackPositionTicks": 0
}
```

---

## Implémentation de référence (squelette)

```csharp
public class EmbyConnector : IMediaServerConnector
{
    private readonly ConnectorConfig _config;
    private readonly HttpClient      _httpClient;
    private string?                  _userId;

    public string ServerType  => "Emby";
    public string ConnectorId => _config.Id;
    public string DisplayName => _config.DisplayName;

    public EmbyConnector(ConnectorConfig config, IHttpClientFactory httpClientFactory, ILogger<EmbyConnector> logger)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        _httpClient.DefaultRequestHeaders.Add("X-Emby-Token", config.ApiKey);
    }

    public async Task<ConnectorTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await _httpClient.GetFromJsonAsync<EmbySystemInfo>("System/Info/Public", ct);
            return ConnectorTestResult.Ok(info?.Version ?? "unknown");
        }
        catch (Exception ex) { return ConnectorTestResult.Fail(ex.Message); }
    }

    // GetUserIdAsync() — appelle Users/AuthenticateByName ou utilise l'ID du token
    // ListItemsAsync() — pagine sur Users/{userId}/Items avec Fields=...,UserData
    // GetMetadataAsync() — GET Users/{userId}/Items/{itemId}?Fields=...,UserData
    // GetStreamUrlAsync() — construit statiquement {ServerUrl}/Videos/{id}/stream?api_key=...
}
```

---

## Différences Emby / Jellyfin

| Aspect | Emby | Jellyfin |
|---|---|---|
| Header auth | `X-Emby-Token` | `X-MediaBrowser-Token` |
| Base path | `/emby/` (si reverse proxy) | `/` |
| UserData | Identique | Identique |
| Stream URL | `/Videos/{id}/stream` | `/Videos/{id}/stream` |

Le futur `JellyfinConnector` (issue #15) sera quasi identique avec ces ajustements.
