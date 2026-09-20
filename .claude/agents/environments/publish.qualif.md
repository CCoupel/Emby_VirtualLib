# Publish QUALIF — Adaptations VirtualLib

> Compagnon de `publish.qualif.template.md` — precise le mecanisme de promotion pour l'artefact
> `VirtualLib.dll` (pas de tarball generique).

```bash
# 1. Verification
test -f "$BUILD_DIR/VirtualLib.dll" || { echo "Aucun candidat trouve — executer /build d'abord"; exit 1; }

# 2. Promotion — copie simple, pas de registre/depot distant
TARGET_DIR="$REPO_ROOT/build/qualif_v$DIR_VERSION"
mkdir -p "$TARGET_DIR"
cp "$BUILD_DIR/VirtualLib.dll" "$TARGET_DIR/VirtualLib.dll"

# 3. Notification
echo "Publication QUALIF terminee - $VERSION -> $TARGET_DIR/VirtualLib.dll"
```

Aucune variable (`REGISTRY_USER`/`REGISTRY_PASSWORD`) necessaire — copie locale uniquement.

## Rollback

```bash
rm -rf "$TARGET_DIR"
```
