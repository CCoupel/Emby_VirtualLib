# Films et Séries

## Arborescences générées

### Films

```
{virtualLibRoot}/
└── {LibraryName}/
    └── {Title} ({Year})/
        ├── {Title} ({Year}).strm
        ├── movie.nfo
        ├── poster.jpg
        └── fanart.jpg
```

### Séries

```
{virtualLibRoot}/
└── {LibraryName}/
    └── {SeriesName}/
        ├── tvshow.nfo
        ├── poster.jpg
        ├── fanart.jpg
        └── Season {XX}/
            ├── season.nfo
            ├── poster.jpg
            └── {SeriesName} - S{XX}E{YY} - {Title}.strm
                {SeriesName} - S{XX}E{YY} - {Title}.nfo
```

---

## Formats NFO

### movie.nfo

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<movie>
  <title>Inception</title>
  <year>2010</year>
  <plot>Un voleur qui s'infiltre dans les rêves...</plot>
  <rating>8.8</rating>
  <runtime>148</runtime>
  <genre>Science-Fiction</genre>
  <genre>Thriller</genre>
  <studio>Warner Bros.</studio>
  <tag>mind-bending</tag>
  <tagline>Your mind is the scene of the crime.</tagline>
  <uniqueid type="imdb" default="true">tt1375666</uniqueid>
  <uniqueid type="tmdb">27205</uniqueid>
  <actor>
    <name>Leonardo DiCaprio</name>
    <role>Cobb</role>
    <type>Actor</type>
  </actor>
  <director>Christopher Nolan</director>
  <trailer>https://www.youtube.com/watch?v=...</trailer>
  <fileinfo>
    <streamdetails>
      <video>
        <codec>h264</codec>
        <width>1920</width>
        <height>1080</height>
        <durationinseconds>8880</durationinseconds>
      </video>
      <audio>
        <codec>ac3</codec>
        <channels>6</channels>
      </audio>
    </streamdetails>
  </fileinfo>
</movie>
```

### episodedetails.nfo

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<episodedetails>
  <title>Pilot</title>
  <season>1</season>
  <episode>1</episode>
  <plot>Walter White, un professeur de chimie...</plot>
  <rating>9.0</rating>
  <uniqueid type="tvdb">349232</uniqueid>
  <fileinfo>
    <streamdetails>
      <video>
        <codec>h264</codec>
        <width>1280</width>
        <height>720</height>
        <durationinseconds>2880</durationinseconds>
      </video>
      <audio>
        <codec>aac</codec>
        <channels>2</channels>
      </audio>
    </streamdetails>
  </fileinfo>
</episodedetails>
```

### tvshow.nfo

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<tvshow>
  <title>Breaking Bad</title>
  <year>2008</year>
  <plot>Un professeur de chimie atteint d'un cancer...</plot>
  <rating>9.5</rating>
  <genre>Crime</genre>
  <genre>Drama</genre>
  <uniqueid type="tvdb">81189</uniqueid>
  <uniqueid type="imdb">tt0903747</uniqueid>
  <actor>
    <name>Bryan Cranston</name>
    <role>Walter White</role>
    <type>Actor</type>
  </actor>
</tvshow>
```

### season.nfo

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<season>
  <title>Season 1</title>
  <seasonnumber>1</seasonnumber>
  <year>2008</year>
</season>
```

---

## Artwork téléchargé

| Niveau | Fichiers |
|---|---|
| Film | `poster.jpg`, `fanart.jpg`, `logo.png`, `landscape.jpg` |
| Show | `poster.jpg`, `fanart.jpg`, `logo.png`, `banner.jpg` |
| Saison | `poster.jpg`, `fanart.jpg` |
| Épisode | `{episode}.jpg` (thumb) |

---

## Contrainte `<fileinfo>` — rappel

Emby ne lance pas ffprobe sur les `.strm`. Sans `<fileinfo>` dans le NFO, aucun `MediaStream` n'est créé.
Le bloc `<fileinfo><streamdetails>` est la **seule voie fiable** pour afficher résolution et codec dans l'UI.

Détails de la stratégie → [core/SYNC.md](../core/SYNC.md#contrainte-critique--fileinfo-et-fichiers-strm).
