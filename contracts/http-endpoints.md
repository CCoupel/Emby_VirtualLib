# Contrats — Endpoints HTTP

> API plugin Emby : DTOs à la ServiceStack décorés `[Route]` + `[Authenticated]`, définis dans `src/VirtualLib/Api/ConfigController.cs`. Ce ne sont pas des controllers ASP.NET.

---

## `POST /virtuallib/connectors` *(modifié — #44)*

**Description** : crée un connecteur.
**Auth** : `[Authenticated]` (session Emby administrateur).

**Champs ajoutés au corps de requête**

```json
{
  "FilterMinHeight": 0,
  "FilterAudioLanguages": "",
  "FilterSubtitleLanguages": "",
  "FilterIgnoreForcedSubtitles": false
}
```

| Champ | Type | Défaut | Description |
|---|---|---|---|
| `FilterMinHeight` | `int` | `0` | Hauteur vidéo minimale en pixels. `0` = règle inactive. |
| `FilterAudioLanguages` | `string` | `""` | Codes langue séparés par des virgules (`"fr,en"`). Vide = règle inactive. |
| `FilterSubtitleLanguages` | `string` | `""` | Idem, pour les sous-titres. |
| `FilterIgnoreForcedSubtitles` | `bool` | `false` | Exclut les sous-titres forcés du test. |

**Réponse 200** : l'objet `ConnectorConfig` créé, incluant `MediaFilter` sous forme d'objet imbriqué.

> **Asymétrie assumée** : les DTOs exposent quatre champs **plats** ; le stockage et la réponse utilisent l'objet imbriqué `MediaFilter`. Motif : le binding d'objets JSON imbriqués par la couche DTO d'Emby n'est éprouvé par aucun endpoint existant du plugin ; un échec silencieux reproduirait le bug `CacheEnabled`. La conversion CSV ↔ `List<string>` est faite dans le contrôleur.

---

## `PUT /virtuallib/connectors/{Id}` *(modifié — #44)*

Mêmes quatre champs ajoutés, même sémantique.

**⚠️ Contrat impératif — remplacement total**
Cet endpoint **reconstruit intégralement** `ConnectorConfig`. Tout champ absent du corps de la requête est réinitialisé à sa valeur par défaut. Deux bugs de production en découlent déjà (`CacheEnabled` jamais persisté, `LocalUserId` effacé par `toggleLibrary`).

Tout client de cet endpoint **doit** envoyer l'intégralité des champs, y compris ceux qu'il ne modifie pas. Les trois constructeurs de payload de `configjs.js` sont concernés :
- `openEditConnector` → sauvegarde du formulaire (`configjs.js:1007-1029`)
- ajout d'un connecteur (`configjs.js:1041-1057`)
- `toggleLibrary` — bascule d'une checkbox de bibliothèque (`configjs.js:776-791`)

**Exception** : `Password` vide signifie « conserver l'existant » (`ConfigController.cs:379`).

---

## `GET /virtuallib/connectors` *(inchangé)*

Retourne la liste des `ConnectorConfig`, désormais avec le sous-objet `MediaFilter`.

---

## `POST /virtuallib/sync`, `POST /virtuallib/connectors/{Id}/sync`, `POST /virtuallib/connectors/{Id}/libraries/{LibraryId}/sync` *(réponse enrichie — #44)*

Les entrées de résultat par bibliothèque exposent un champ supplémentaire :

| Champ | Type | Description |
|---|---|---|
| `ItemsFiltered` | `int` | Items écartés par les règles de filtrage |

Rétrocompatible : un client ignorant ce champ n'est pas affecté.

---

## `GET /virtuallib/settings` · `PUT /virtuallib/settings` *(modifiés — #44)*

| Champ ajouté | Type | Défaut | Description |
|---|---|---|---|
| `OrphanCleanupEnabled` | `bool` | `false` | Active la brique de nettoyage des orphelins (partagée avec #12). Désactivée par défaut. |
