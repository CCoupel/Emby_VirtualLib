# Livres audio & Ebooks

## Livres audio (AudioBook)

### Arborescence générée

```
{virtualLibRoot}/
└── {LibraryName}/
    └── {BookTitle} ({Year})/
        ├── album.nfo
        ├── folder.jpg          (couverture du livre)
        ├── fanart.jpg
        ├── {Chapitre 01}.strm
        ├── {Chapitre 01}.jpg   (artwork du chapitre si disponible)
        ├── {Chapitre 02}.strm
        └── ...
```

### Format NFO — album.nfo

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<album>
  <title>Harry Potter et la Chambre des Secrets</title>
  <year>2000</year>
  <plot>Harry Potter retourne à Poudlard...</plot>
  <genre>Fantasy</genre>
  <albumartist>J.K. Rowling</albumartist>
  <albumartist>Jim Dale</albumartist>
</album>
```

L'élément `<albumartist>` contient les auteurs/narrateurs propagés depuis `AlbumArtist` ou `People[Author]` du serveur source.

### Providers de métadonnées locaux

Emby ne lit pas les `.nfo` Kodi pour les types `Audio` (chapitres) et `Folder` dans une bibliothèque Audiobooks. Trois providers custom comblent ces lacunes :

| Provider | Type Emby | Fichier lu | Rôle |
|---|---|---|---|
| `AudioBookNfoProvider` | `Audio` | `album.nfo` (dossier parent) | Injecte `Album`, `AlbumArtists`, `ProductionYear` sur chaque chapitre |
| `AudioBookFolderNfoProvider` | `Folder` | `album.nfo` (dans le dossier) | Injecte titre, synopsis, genres, auteurs sur le container du livre |

Ces providers sont auto-découverts par Emby via le plugin assembly (pas de registration manuelle).

### Contrainte DI SimpleInjector

Emby utilise SimpleInjector qui **n'enregistre pas** `ILogger<T>` générique. Les providers ne doivent pas injecter `ILogger<T>` dans leur constructeur — cela provoque une `ActivationException` au chargement du plugin. Utiliser `NullLogger<T>.Instance`.

### Injection directe en DB

Pour les items `Audio` (chapitres), Emby ne re-probe pas les `.strm`. Les métadonnées sont injectées directement après le scan via `ILibraryManager.UpdateItem` :
- `RunTimeTicks` — durée du chapitre
- `Album` — titre du livre (et non le titre du chapitre)
- `AlbumArtists` — auteurs

> **Important** : injecter `Album = bookTitle` et non `Name = chapterTitle` sur les items Audio. C'est le champ `Album` qui permet à Emby de regrouper les chapitres sous le bon livre.

---

## Ebooks / PDF / ePub / Mobi

### Arborescence générée

```
{virtualLibRoot}/
└── {LibraryName}/
    └── {Title} ({Year})/
        ├── {Title} ({Year}).epub     (fichier réel téléchargé depuis la source)
        ├── {Title} ({Year}).nfo
        └── poster.jpg
```

Le fichier `.epub` (ou `.pdf`, `.mobi`) est téléchargé depuis le serveur source via `DownloadFileToPathAsync`. C'est un fichier réel, pas un `.strm`.

### Format NFO — book.nfo

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<book>
  <title>Dune</title>
  <year>1965</year>
  <plot>Sur la planète désertique Arrakis...</plot>
  <genre>Science-Fiction</genre>
  <author>Frank Herbert</author>
  <uniqueid type="isbn">9780441013593</uniqueid>
</book>
```

### Provider de métadonnées

| Provider | Type Emby | Fichier lu | Rôle |
|---|---|---|---|
| `BookNfoProvider` | `Book` | `{filename}.nfo` | Injecte les métadonnées complètes pour les ebooks |

---

## Polling loop post-scan

Pour les livres audio et ebooks, la boucle de polling Phase 2 attend que Emby ait scanné les items avant d'injecter les métadonnées :

```
Délai polling : 2 secondes entre chaque vérification
Timeout total : 5 minutes
```

Cela évite d'avoir à déclencher une seconde sync manuelle pour que les métadonnées apparaissent.

---

## Artwork

| Source | Fichier local |
|---|---|
| Container du livre (Primary) | `folder.jpg` |
| Container du livre (Backdrop) | `fanart.jpg` |
| Chapitre (Primary) | `{chapitre}.jpg` |

**Fallback** : si le container AudioBook n'a pas d'image Primary, l'artwork du premier chapitre est utilisé.

**Artwork découplé de la condition NFO** : les images sont téléchargées même si `album.nfo` existe déjà (mode RemoteSync incrémental).
