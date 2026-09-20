# deployer — Adaptations VirtualLib

> Compagnon de `.claude/agents/deploy.template.md` — ne contredit pas le template, precise le
> mecanisme concret pour VirtualLib. Lu par le teammate `deployer` en plus du template.

## Tache BUILD — mecanisme concret

Artefact candidat : `VirtualLib.dll` (pas de tarball/image Docker). SDK .NET 6 installe dans le
profil utilisateur (`~/AppData/Local/Microsoft/dotnet/` sur poste Windows/WSL de dev — pas dans
`Program Files`, qui ne contient que le runtime).

```bash
# 1. Verification
git status                                    # Clean working directory
dotnet test tests/VirtualLib.Tests/           # Tests passent

# 2. Increment de version (a+1) — voir project-config.json "version" (X.Y.Z.a)

# 3. Build — dossier candidate_vX.Y.Z a la racine du repo (voir Tache BUILD du template)
REPO_ROOT=$(git rev-parse --show-toplevel)
BUILD_DIR="$REPO_ROOT/build/candidate_v$DIR_VERSION"
mkdir -p "$BUILD_DIR"
dotnet build "$REPO_ROOT/src/VirtualLib" --configuration Release --output "$BUILD_DIR"
# Verification post-build : "$BUILD_DIR/VirtualLib.dll" present, date recente, "0 Erreur(s)"
# dans la sortie dotnet build.

# 4. Notification
echo "Build termine - $VERSION -> $BUILD_DIR/VirtualLib.dll"
```

Artefact publie par PUBLISH QUALIF/PROD : `VirtualLib.dll` (nom de fichier fixe, seul le
contenu change entre versions — pas de suffixe de version dans le nom, contrairement au
`app-$VERSION.tar.gz` generique du template).
