# ProxyController

Endpoint HTTP enregistré dans le pipeline ASP.NET d'Emby.

```
GET /virtuallib/proxy/{connectorId}/{libraryId}/{itemId}
```

**Le DTO doit être marqué `[Unauthenticated]`** : ffprobe probe les STRM sans token. Sans cet attribut, Emby retourne 401 et la lecture échoue immédiatement. Le contrôle d'accès est assuré par le plugin lui-même (voir ci-dessous).

---

## Comportement

1. Valide que le connector existe et est actif
2. Valide que `libraryId` est configurée sur le connector
3. Si token présent → vérifie que l'utilisateur a accès à la bibliothèque virtuelle Emby (fail-closed)
4. Si pas de token → autorisé uniquement si IP privée (RFC 1918) **et** User-Agent interne (`Lavf/*` = ffprobe, absent = Emby .NET client)
5. Demande l'URL de stream à `connector.GetStreamUrlAsync(itemId)`
6. Copie les headers pertinents (Content-Type, Content-Length, Accept-Ranges, Content-Range)
7. Supporte les `Range` headers pour permettre le seek
8. Pipe le body vers la réponse client via `Stream.CopyToAsync`

---

## Modèle de sécurité

| Requête | Résultat |
|---|---|
| Token valide + accès bibliothèque | ✅ autorisé |
| Token valide + pas d'accès | ❌ 401 |
| Sans token + IP privée + UA `Lavf/*` | ✅ autorisé (ffprobe) |
| Sans token + IP privée + pas de UA | ✅ autorisé (Emby interne) |
| Sans token + IP privée + UA navigateur | ❌ 401 |
| Sans token + IP publique | ❌ 401 |

**Note DI** : `ILogger<T>` n'est pas enregistré dans SimpleInjector pour les controllers Emby. Le controller utilise `NullLogger<ProxyController>.Instance`.

---

## Support Range (seek)

```csharp
// Forwarde le header Range de la requête client vers la source
if (!string.IsNullOrEmpty(range))
    remoteRequest.Headers.TryAddWithoutValidation("Range", range);
// Retourne 206 Partial Content si la source répond 206
```

---

## Piège critique — Content-Range

En .NET `HttpClient`, `Content-Range` est dans `response.Content.Headers` (HttpContentHeaders), **pas** dans `response.Headers` (HttpResponseHeaders). Ne pas le forwarder cause des erreurs graves :

- ffprobe lit un chunk de N bytes (ex: les 1663 derniers octets d'un MKV)
- Voit Content-Length=1663 sans Content-Range → croit que le fichier fait 1663 bytes
- Emby stocke `Size=1663` dans MediaSource
- ffmpeg demande 1663 bytes, les obtient, voit EOF → "File ended prematurely"

```csharp
// ✅ Correct
var contentRangeHeader = remoteResponse.Content.Headers.ContentRange;
if (contentRangeHeader != null)
    forwardHeaders["Content-Range"] = contentRangeHeader.ToString();

// ❌ Incorrect — Content-Range n'est jamais dans response.Headers
var cr = remoteResponse.Headers.GetValues("Content-Range");
```

---

## Gestion des déconnexions client

ffprobe ferme la connexion après avoir lu quelques Mo (suffisant pour analyser les métadonnées). `PipeWriterStream.DisposeAsync()` lève une exception si Content-Length était 22 Go mais seulement quelques Mo ont été écrits.

```csharp
try { await remoteStream.CopyToAsync(outputStream, ct); }
catch (OperationCanceledException ex) when (ex.CancellationToken != ct)
{
    // Client closed connection — not Emby's cancellation token
}
catch (IOException ex) when (!ct.IsCancellationRequested)
{
    // Broken pipe — normal for ffprobe
}
finally { try { await outputStream.DisposeAsync(); } catch { } }
```
