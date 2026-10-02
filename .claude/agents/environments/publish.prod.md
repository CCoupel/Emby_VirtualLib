# publish PROD — Adaptations VirtualLib

> Compagnon de `.claude/agents/environments/publish.prod.template.md` — derogations projet, a
> appliquer EN PLUS du template (elles priment sur les commandes du template en cas d'ecart).

## Lecture / ecriture de la version

`version_file` = `.claude/project-config.json`, champ JSON `"version"` (pas un fichier texte :
`cat` ne convient pas). `src/VirtualLib/VirtualLib.csproj` (`<Version>`) en est le miroir,
aligne par `deployer` a chaque BUILD.

```bash
DEV_VERSION=$(jq -r .version .claude/project-config.json)   # ex: 1.12.0.3
```

## Format de version PROD : X.Y.Z.0 (4 segments)

Le catalogue Emby exige 4 segments (issue #24) : en prod le dernier segment est fixe a `0`.
Ne pas utiliser `cut -d. -f1-3` + tag a 3 segments comme dans le template.

```bash
VERSION="$(echo "$DEV_VERSION" | cut -d. -f1-3).0"          # ex: 1.12.0.0
# Ecrire $VERSION dans .claude/project-config.json ET dans <Version> de
# src/VirtualLib/VirtualLib.csproj avant le merge :
jq --arg v "$VERSION" '.version=$v' .claude/project-config.json > .claude/pc.tmp && mv .claude/pc.tmp .claude/project-config.json
sed -i -E "s|<Version>[^<]+</Version>|<Version>$VERSION</Version>|" src/VirtualLib/VirtualLib.csproj
```

Le tag officiel est `v$VERSION` (ex: `v1.12.0.0`), conforme au pattern
`v[0-9]+.[0-9]+.[0-9]+.[0-9]+` de `.github/workflows/release.yml`. Le message de merge et le tag
utilisent la meme valeur 4 segments.

## Verification CHANGELOG

Le test `grep -q "$DEV_VERSION" CHANGELOG.md` du template doit chercher la version PROD
(`$VERSION`, 4 segments), pas la version de build `X.Y.Z.a`.
