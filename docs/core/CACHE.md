# Cache local — Architecture

## 1. Vue d'ensemble

Le cache local stocke les flux médias proxifiés sous forme de **segments de taille variable** sur le disque de l'hôte Emby. L'unité de flush est le **flush interval** (paramètre `CacheChunkSizeMb`, 2 Mo par défaut) : pendant le streaming, les octets sont accumulés dans un fichier temporaire `.tmp` et commités sur disque toutes les `flushIntervalBytes` octets exactement, à la frontière d'un chunk.

Trois propriétés centrales du mécanisme :

- **Alignement strict** : chaque segment commence et se termine exactement sur une frontière de chunk. Les octets arrivant avant la première frontière alignée (`cacheFrom`) sont transmis au client mais non écrits sur disque.
- **Merge automatique** : après chaque commit, le segment est fusionné avec ses voisins immédiats (gauche et/ou droit) par concaténation physique des fichiers `.bin`. La lecture séquentielle converge ainsi vers un segment unique couvrant tout le fichier.
- **Tail conditionnel** : un segment incomplet (< `flushIntervalBytes`) en fin de stream n'est commité que si `segStart + segBytes >= TotalSize`. Dans le cas contraire, le fichier `.tmp` est supprimé sans laisser de trace dans le manifest.

Le cache est **désactivé par défaut** (`CacheEnabled = false`) et peut être surchargé par connecteur.

---

## 2. Structure sur disque

```
{cacheRoot}/
  {connectorId}/
    {itemId}/
      manifest.json                                  ← état du cache (écriture atomique .tmp → rename)
      seg_00000000000000000000_<guid>.bin             ← segment [0, flushInterval)
      seg_00000000002097152000_<guid>.bin             ← segment [2 097 152, ...)
      seg_00000000004194304000_<guid>.bin             ← segment [4 194 304, ...)
```

**Nommage des fichiers** :

- Temporaire (en cours d'écriture) : `seg_{start:D20}_{Guid.NewGuid():N}.tmp`
- Définitif (commité) : `seg_{start:D20}_{Guid.NewGuid():N}.bin`

`start` est l'offset de début du segment dans le fichier source, encodé sur 20 chiffres avec zéros de tête (tri lexicographique = tri numérique). Le GUID rend le nom unique et évite les collisions si un segment est recréé après suppression.

`cacheRoot` = `PluginConfiguration.CacheRootPath`, ou `applicationPaths.CachePath/virtuallib-cache` si ce champ est vide. `connectorId` et `itemId` sont sanitisés : tout caractère invalide pour un nom de fichier est remplacé par `_`.

---

## 3. ChunkManifest

### Champs JSON

| Champ | Type | Description |
|-------|------|-------------|
| `ItemId` | `string` | Identifiant de l'item sur le serveur source |
| `ConnectorId` | `string` | Identifiant du connecteur (ex : `"my-emby-server"`) |
| `TotalSize` | `long` | Taille totale du fichier source en octets ; `-1` si inconnue |
| `ChunkSize` | `int` | Flush interval en octets au moment de la création du manifest (défaut 2 097 152) ; sert au calcul de `FirstCoveredChunk`/`LastCoveredChunk` sur chaque segment |
| `ContentType` | `string` | Type MIME retourné par le serveur source (ex : `"video/x-matroska"`) |
| `SourceUrl` | `string` | URL distante depuis laquelle le contenu est streamé |
| `Segments` | `List<CachedSegment>` | Liste triée par `Start` sans chevauchements |
| `CreatedAt` | `DateTime` | Date de création du manifest (UTC) |
| `LastAccessAt` | `DateTime` | Date du dernier accès en lecture ou écriture (UTC) |

### Champs calculés (`[JsonIgnore]`)

| Propriété | Calcul |
|-----------|--------|
| `TotalChunks` | `ceil(TotalSize / ChunkSize)` ; `-1` si l'un des opérandes est ≤ 0 |
| `IsComplete` | `TotalSize > 0 && Segments.Count == 1 && Segments[0].Start == 0 && Segments[0].Length == TotalSize` |

### Exemple complet

```json
{
  "ItemId": "a3f8c1e2d09b4571",
  "ConnectorId": "emby-nas",
  "TotalSize": 8589934592,
  "ChunkSize": 2097152,
  "ContentType": "video/x-matroska",
  "SourceUrl": "http://192.168.1.10:8096/Videos/a3f8c1e2d09b4571/stream.mkv",
  "Segments": [
    {
      "Start": 0,
      "Length": 6291456,
      "FileName": "seg_00000000000000000000_3f2a1b9c4d5e6f7a8b9c0d1e2f3a4b5c.bin",
      "FirstCoveredChunk": 0,
      "LastCoveredChunk": 2
    },
    {
      "Start": 8388608,
      "Length": 2097152,
      "FileName": "seg_00000000008388608000_7e6d5c4b3a2918070605040302010000.bin",
      "FirstCoveredChunk": 4,
      "LastCoveredChunk": 4
    }
  ],
  "CreatedAt": "2026-04-08T09:00:00Z",
  "LastAccessAt": "2026-04-08T11:42:17Z"
}
```

### Invariants

- Un fichier `.bin` est toujours complet : il est écrit sous son nom `.tmp` puis renommé atomiquement en `.bin` ; un `.bin` partiel n'existe jamais.
- `Segments` est toujours trié par `Start` croissant, sans chevauchement.
- Deux segments adjacents (`s1.End == s2.Start`) sont automatiquement fusionnés après chaque commit.
- Quand le fichier est intégralement caché, `Segments` contient exactement un élément couvrant `[0, TotalSize)`.
- Le manifest en mémoire (`_manifests[key]`) est la source de vérité ; le fichier `manifest.json` sur disque en est la copie persistante, toujours cohérente avec l'état en mémoire après chaque flush.

---

## 4. CachedSegment

| Champ | Type | Description |
|-------|------|-------------|
| `Start` | `long` | Offset de début dans le fichier source (inclus) |
| `Length` | `long` | Nombre d'octets couverts par ce segment |
| `FileName` | `string` | Nom du fichier `.bin` dans le répertoire de l'item |
| `FirstCoveredChunk` | `int` | Index du premier chunk **entièrement** couvert par le segment ; `-1` si aucun chunk complet n'est couvert |
| `LastCoveredChunk` | `int` | Index du dernier chunk entièrement couvert (inclus) ; `-1` si aucun |

Propriétés calculées (`[JsonIgnore]`) :

- `End` = `Start + Length` (offset exclusif)
- `EndChunk` = `LastCoveredChunk + 1` si `LastCoveredChunk >= 0`, sinon `-1`

### Définition de "chunk entièrement couvert"

Le chunk d'index `i` couvre les octets `[i * ChunkSize, min((i+1) * ChunkSize, TotalSize))`. Il est **entièrement couvert** par un segment si son intervalle complet est inclus dans `[seg.Start, seg.End)`. `ComputeChunkCoverage` itère depuis le premier chunk dont le début est ≥ `seg.Start` et s'arrête dès qu'un `chunkEnd > seg.End` ou que `i * ChunkSize >= TotalSize`.

---

## 5. Couverture de chunks

### Calcul de `FirstCoveredChunk` / `LastCoveredChunk`

`ChunkManifest.ComputeChunkCoverage(seg, chunkSize, totalSize)` :

```
firstChunk = ceil(seg.Start / chunkSize)   // premier chunk dont le début >= seg.Start
last = -1
pour i = firstChunk, firstChunk+1, ... :
    si i * chunkSize >= totalSize → stop   // chunk fantôme au-delà de l'EOF
    chunkEnd = min((i+1)*chunkSize, totalSize)
    si chunkEnd > seg.End → stop           // ce chunk déborde du segment
    last = i
    si chunkEnd >= totalSize → stop        // dernier chunk du fichier atteint

FirstCoveredChunk = last >= 0 ? firstChunk : -1
LastCoveredChunk  = last
```

`RefreshChunkCoverage()` appelle cette fonction pour chaque segment de la liste. Elle est invoquée après tout merge ou ajout de segment, et systématiquement à l'initialisation.

### Cas légitimes vs parasites

Un segment avec `FirstCoveredChunk == -1` peut être :

- **Tail légitime** : commence exactement sur une frontière de chunk (`seg.Start % ChunkSize == 0`) et s'étend jusqu'à `TotalSize` (`seg.End >= TotalSize`). C'est le cas du dernier flush d'un fichier dont la taille n'est pas un multiple de `ChunkSize`. Ce segment est conservé.
- **Segment parasite** : résidu d'un streaming interrompu avant la première frontière alignée, ou artefact d'avant l'introduction de l'alignement strict. Ce segment est supprimé par `RemoveUnalignedSegments`.

### Définition d'un tail légitime

```csharp
seg.End >= manifest.TotalSize
    && seg.Start % manifest.ChunkSize == 0
```

Les deux conditions sont nécessaires : un segment qui atteint `TotalSize` mais ne commence pas sur une frontière est également parasite.

---

## 6. Flux d'alimentation (`CopyWithCacheAsync`)

### Alignement initial (`cacheFrom`)

```csharp
cacheFrom = startOffset % flushIntervalBytes == 0
    ? startOffset
    : (startOffset / flushIntervalBytes + 1) * flushIntervalBytes;
```

Si le client a demandé `bytes=500000-`, `cacheFrom` sera 2 097 152 (la frontière suivante). Les octets `[500000, 2097152)` sont transmis au client immédiatement mais ne sont pas écrits sur disque.

### Boucle interne

Pour chaque `read` octets lus depuis la source :

1. Tout le buffer est immédiatement écrit dans `destination` (le client reçoit les données sans latence).
2. Une boucle interne sur `bufPos` traite le buffer octet-par-octet en trois états :
   - **Skip** : `globalPos < cacheFrom` → avance `bufPos` jusqu'à `cacheFrom`, sans rien écrire sur disque.
   - **Open** : `tmpStream == null` → ouvre un fichier `seg_{segStart:D20}_{guid}.tmp`.
   - **Write** : écrit `min(read - bufPos, flushIntervalBytes - segBytes)` octets dans `tmpStream`.
3. Quand `segBytes >= flushIntervalBytes` : flush exact à la frontière de chunk, fermeture du `.tmp`, appel à `CommitSegmentAsync`, réinitialisation de `segStart` et `segBytes`.

### Commit du tail

Après la fin de la boucle source (`read == 0`), si `segBytes > 0` :

```csharp
isFileTail = manifest.TotalSize <= 0 || segStart + segBytes >= manifest.TotalSize
```

- Si `isFileTail == true` : commit du segment tel quel (il constitue la fin réelle du fichier).
- Si `isFileTail == false` : le fichier `.tmp` est supprimé ; rien n'est ajouté au manifest.

### Gestion des erreurs

Toute exception dans la boucle ferme et supprime le `.tmp` courant avant de propager l'exception.

---

## 7. Merge de segments adjacents

`MergeAndCommitSegmentAsync` est appelée sous le verrou manifest après chaque rename `.tmp` → `.bin`.

**Merge gauche** : recherche un segment `left` tel que `left.End == newSeg.Start`. Si trouvé, le contenu de `newSeg.FileName` est **ajouté en mode Append** à `left.FileName`, puis `newSeg.FileName` est supprimé. La progression `(mergedStart, mergedLength, mergedFileName)` est mise à jour.

**Merge droit** : recherche un segment `right` tel que `right.Start == mergedStart + mergedLength`. Si trouvé, le contenu de `right.FileName` est ajouté en Append à `mergedFilePath`, puis `right.FileName` est supprimé.

Le segment résultant `(mergedStart, mergedLength, mergedFileName)` est ajouté à `manifest.Segments`, la liste est re-triée par `Start`, et `RefreshChunkCoverage()` est appelée.

En cas d'échec d'un merge (exception I/O), l'erreur est loggée et le nouveau segment est conservé sans fusion (non-fatal) ; le merge sera retentable au prochain commit.

---

## 8. Validation (`ValidateItemAsync` / `InitializeAsync`)

### `ValidateItemAsync` (3 passes, sous verrou manifest)

**Passe 1 — Segments fantômes** : supprime de `manifest.Segments` tout segment dont le fichier `.bin` est absent sur disque.

**Passe 2 — Segments non-alignés** (`RemoveUnalignedSegments`) :

- `RefreshChunkCoverage()` est appelée en premier pour garantir des valeurs à jour.
- *Pass 1 interne* : supprime les segments avec `FirstCoveredChunk == -1` qui ne sont pas des tails légitimes (fichier `.bin` supprimé).
- *Pass 2 interne* : pour chaque segment restant non-tail, calcule `alignedEnd = (LastCoveredChunk + 1) * ChunkSize`. Si `seg.Length != alignedEnd - seg.Start`, tronque physiquement le fichier à `usableLength` via `FileStream.SetLength` et met à jour `seg.Length`. Si le fichier est plus petit que `usableLength`, le segment est supprimé.

**Passe 3 — Orphelins** : liste tous les `*.bin` du répertoire item et supprime ceux non référencés par un segment du manifest.

Si au moins une modification a été effectuée (`dirty == true`), `RefreshChunkCoverage()` puis `FlushManifestAsync` sont appelées.

### `InitializeAsync` (au démarrage du plugin)

1. Supprime tous les fichiers `*.tmp` trouvés récursivement sous `_cacheRoot` (résidus de crash).
2. Pour chaque `manifest.json` trouvé sur disque :
   - Supprime les segments fantômes.
   - Appelle `RemoveOverlappingSegments` (gestion des manifests legacy avec chevauchements).
   - Appelle `RemoveUnalignedSegments`.
   - Supprime les `.bin` orphelins.
   - Appelle `RefreshChunkCoverage()`.
   - **Flush systématique** du manifest (migration de champs, même si aucun changement structurel n'a été détecté).
   - Charge le manifest en mémoire dans `_manifests`.

Le flush systématique à l'init sert de mécanisme de migration : il garantit que les manifests écrits par des versions antérieures du plugin sont réécrits avec le schéma JSON actuel (renommages de champs, nouveaux champs avec valeurs par défaut).

---

## 9. Cycle de vie dans `ProxyController`

### Avant la vérification du cache hit

```csharp
await Plugin.Cache!.ValidateItemAsync(connectorConfig.Id, request.ItemId);
var manifest = await Plugin.Cache!.GetManifestAsync(connectorConfig.Id, request.ItemId);
```

`ValidateItemAsync` est appelée **avant** de vérifier `IsRangeCached` : cela garantit qu'aucun segment fantôme ne provoque un hit erroné suivi d'une erreur I/O lors de `ServeCachedRangeAsync`.

### Cache hit

Si `IsRangeCached(manifest, cachedStart, cachedEnd)` est vrai :

- La réponse est construite sans créer de connecteur (pas d'appel réseau).
- `ServeCachedRangeAsync` lit directement le `.bin` avec `FileShare.ReadWrite` (lecture pendant un éventuel merge concurrent).
- Après la réponse, `ValidateItemAsync` est appelée en **fire-and-forget** (`_ = Plugin.Cache?.ValidateItemAsync(..., CancellationToken.None)`) pour détecter d'éventuels orphelins créés entre deux requêtes.

### Cache miss (chemin proxy)

- `EnsureManifestAsync` est appelée dès que `Content-Length` est connue depuis les headers upstream (idempotent).
- `CopyWithCacheAsync` remplace `remoteStream.CopyToAsync(outputStream)` : le stream est transmis au client ET mis en cache simultanément.
- Après la fin du stream, `ValidateItemAsync` est appelée en fire-and-forget.

### Activation conditionnelle

Le cache n'est activé que si `config.CacheEnabled && connectorConfig.CacheEnabled && Plugin.Cache != null`. Si l'une des conditions est fausse, `remoteStream.CopyToAsync(outputStream)` est utilisé directement, sans écriture sur disque.

---

## 10. Concurrence

| Mécanisme | Rôle |
|-----------|------|
| `SemaphoreSlim(1,1)` par item (`_manifestLocks`) | Sérialise les modifications du manifest en mémoire et les flushes disque |
| `CancellationToken.None` après `File.Move` | Après le rename `.tmp` → `.bin`, le commit du manifest est non-annulable : le fichier est sur disque et le manifest doit le refléter même si le client a déconnecté |
| Double-check `IsRangeCached` / `StartsInsideExistingSegment` sous verrou | Évite les commits en double si deux requêtes couvrent le même range simultanément |
| `FileShare.ReadWrite` dans `ServeCachedRangeAsync` | Permet la lecture d'un `.bin` pendant un merge concurrent (Append mode) |
| Écriture atomique `.tmp` → rename | Un `.bin` est toujours complet ; un lecteur ne peut jamais ouvrir un fichier partiel |
| `ArrayPool<byte>.Shared` pour les buffers | Évite les allocations répétées de buffers 64 Ko pendant le streaming |

`StartsInsideExistingSegment` (`Segments.Any(s => s.Start <= rangeStart && s.End > rangeStart)`) détecte le cas où un nouveau segment commencerait à l'intérieur d'un segment déjà commité. Ce cas se produit si deux requêtes couvrent un range se chevauchant et que la seconde arrive après le `File.Move` mais avant l'entrée dans le verrou.

---

## 11. Changement de taille de flush interval (`ResizeSegmentsToChunkBoundariesAsync`)

Appelée sous le verrou manifest depuis `EnsureManifestAsync` quand `existing.ChunkSize != chunkSizeBytes` et que des segments existent déjà.

Pour chaque segment :

1. `alignedStart = ceil(seg.Start / newChunkSize) * newChunkSize` — premier octet de chunk complet avec la nouvelle taille.
2. `alignedEnd` :
   - Si tail légitime : `TotalSize` (le tail est conservé intégralement).
   - Sinon : `floor(seg.End / newChunkSize) * newChunkSize`.
3. Si `alignedStart >= alignedEnd` : aucun chunk complet ne tient → segment supprimé.
4. Si `skipHead > 0` (le début du fichier actuel précède `alignedStart`) : crée un nouveau fichier `seg_{alignedStart:D20}_{guid}.bin` en copiant depuis l'offset `skipHead`, puis supprime l'ancien fichier.
5. Si `dropTail > 0` (le fichier dépasse `alignedEnd`) et `skipHead == 0` : tronque en place via `FileStream.SetLength`.
6. Met à jour `seg.Start`, `seg.Length`, et éventuellement `seg.FileName`.

Après le traitement de tous les segments, `manifest.ChunkSize` est mis à jour avec `newChunkSize`, la liste est re-triée, et `RefreshChunkCoverage()` est appelée.

---

## 12. API `ICacheManager`

| Méthode | Description |
|---------|-------------|
| `EnsureManifestAsync` | Crée ou retourne le manifest existant pour un item. Idempotent. Met à jour `TotalSize` si précédemment inconnu. Déclenche un resize si `ChunkSize` a changé. |
| `GetManifestAsync` | Retourne le manifest en mémoire, ou le charge depuis disque si absent du dictionnaire. Retourne `null` si non caché. |
| `IsRangeCached` | `true` si un seul segment couvre intégralement `[rangeStart, rangeEnd]` (bornes incluses). |
| `ServeCachedRangeAsync` | Lit et envoie `[rangeStart, rangeEnd]` depuis le fichier `.bin` correspondant. Utilise `ArrayPool` + `FileShare.ReadWrite`. |
| `CopyWithCacheAsync` | Copie `source` vers `destination` en write-through, avec alignement et flush par intervalles. |
| `ValidateItemAsync` | 3 passes : fantômes, non-alignés, orphelins. Flush si modifié. |
| `InvalidateAsync` | Supprime le répertoire item entier et retire le manifest du dictionnaire. |
| `PromoteToFileAsync` | Copie le segment unique (`IsComplete == true`) vers `destPath`. |
| `InitializeAsync` | Nettoyage au démarrage : suppression des `.tmp`, validation et flush de tous les manifests sur disque. |
| `GetStats` | Retourne `TotalItems`, `CompleteItems`, `TotalSizeBytes` calculés sur `_manifests.Values`. |

---

## 13. Configuration

### `PluginConfiguration` (global)

| Paramètre | Type | Défaut | Description |
|-----------|------|--------|-------------|
| `CacheEnabled` | `bool` | `false` | Active le cache globalement. Si `false`, aucun flux n'est mis en cache. |
| `CacheRootPath` | `string` | `""` | Chemin racine du cache. Vide = `applicationPaths.CachePath/virtuallib-cache`. |
| `CacheMaxSizeGb` | `long` | `50` | Taille maximale totale en Go. Non encore appliquée (éviction Phase 2). |
| `CacheChunkSizeMb` | `int` | `2` | Taille du flush interval en Mo. Contrôle la granularité des segments. |
| `CacheTtlDays` | `int` | `30` | Durée de vie avant éviction en jours. Non encore appliquée (Phase 2). |

### `ConnectorConfig` (par connecteur)

| Paramètre | Type | Défaut | Description |
|-----------|------|--------|-------------|
| `CacheEnabled` | `bool` | `true` | Active ou désactive le cache pour ce connecteur uniquement. N'a d'effet que si le `CacheEnabled` global est `true`. |

---

## Optimisations identifiées

### Pending segments (implémenté)

**Problème** : si N clients demandent simultanément le même chunk non encore caché, chacun ouvre une connexion vers le serveur source et télécharge les mêmes octets N fois.

**Mécanisme** : à l'ouverture du fichier `.tmp` dans `CopyWithCacheAsync`, une `TaskCompletionSource<bool>` est enregistrée dans `_pendingSegments` (clé `connectorId:itemId:segStart`). Avant d'ouvrir une connexion vers la source, `ProxyController` appelle `WaitForPendingSegmentAsync` : si une TCS existe, le client attend sans consommer de bande passante. À la fin du commit (`CommitSegmentAsync` → rename `.tmp` → `.bin`), la TCS est résolue à `true` ; si le segment est jeté (tail non-légitimé ou discard), elle est résolue à `false`.

**Règle des 50%** : en cas de déconnexion client (`IOException` / `OperationCanceledException`), si le chunk est rempli à plus de 50 % **ou** qu'un autre client attend, le download continue depuis la source (avec `CancellationToken.None`) jusqu'à la frontière du chunk. Cela évite de jeter un travail presque terminé.

**Comportement des waiters** : `WaitForPendingSegmentAsync` attend la TCS partagée sans l'annuler si son propre `CancellationToken` est révoqué (un `cancelTcs` personnel est utilisé avec `Task.WhenAny`). En cas d'annulation ou d'exception, la méthode retourne `false` : le caller retombe dans le chemin proxy normal.

**Impact** : bande passante source divisée par N pour des accès concurrents au même chunk ; latence légèrement augmentée pour les waiters (ils attendent la fin du chunk, typiquement < 1 s pour un chunk de 2 Mo).

### Retry cache mid-stream (planifié)

**Problème** : quand `CopyWithCacheAsync` vient de commettre le chunk `[A, A+chunkSize)` et que le chunk suivant `[A+chunkSize, ...)` a déjà été caché par une requête précédente, on continue quand même à lire depuis la source au lieu de servir depuis le cache.

**Mécanisme envisagé** : juste avant d'ouvrir le prochain `.tmp`, vérifier `IsRangeCached(manifest, segStart, segStart + flushIntervalBytes - 1)`. Si vrai, interrompre la boucle source et renvoyer au client les octets restants depuis le cache. Gain marginal (~3 lignes de code).

**Priorité** : faible — le cas se produit rarement en pratique (la plupart des lectures sont séquentielles depuis le début).
