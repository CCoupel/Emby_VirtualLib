# Deploy PROD — Adaptations VirtualLib

> Compagnon de `deploy.prod.template.md` — meme mecanisme que `deploy.qualif.md` (copie directe
> du `.dll` via `kubectl cp`, pas de Helm), transpose au deployment `emby` (production).
>
> **⚠ NON VALIDE EN CONDITIONS REELLES** — contrairement a la procedure QUALIF (testee sur de
> multiples cycles, voir `.claude/memory/MEMORY.md`), cette procedure PROD est deduite par
> analogie depuis QUALIF (CLAUDE.md mentionne `kubectl → emby2/emby, namespace media` mais aucun
> deploiement PROD via ce mecanisme n'a encore ete trace). A verifier/confirmer manuellement
> avant le premier `/deploy prod` reel.
>
> **Correction 2026-09-20** : la version initiale referencait par erreur
> `build/qualif_v$DIR_VERSION/` (copie du mecanisme QUALIF) comme source de l'artefact. PROD
> publie en mode `rebuild-ci` (voir `publish.prod.template.md`) — l'artefact n'existe que comme
> asset de la GitHub Release taguee, jamais dans un dossier `build/` local. Corrige pour
> telecharger via `gh release download`. La mecanique kubectl (cp/restart/rollout) reste
> neanmoins non testee en conditions reelles.

> **Namespace** : `media`. Kubeconfig : `private/kubeconfig.yml`. Deployment cible : `emby`
> (jamais `emby2`, qui est QUALIF).

```bash
REPO_ROOT=$(git rev-parse --show-toplevel)
# PROD publie en mode rebuild-ci (voir publish.prod.template.md) : la CI reconstruit depuis le
# tag et attache VirtualLib.dll comme asset de la GitHub Release — rien n'est ecrit localement
# sous build/. Le tag officiel PROD est toujours a 4 segments, dernier fixe a 0 (issue #24,
# catalog-compliance emby) : v$DIR_VERSION.0, ex. v1.11.0.0.
TAG="v$DIR_VERSION.0"
DOWNLOAD_DIR="$REPO_ROOT/build/prod_v$DIR_VERSION"
mkdir -p "$DOWNLOAD_DIR"
gh release download "$TAG" --pattern "VirtualLib.dll" --dir "$DOWNLOAD_DIR" --clobber \
  || { echo "Aucune release GitHub $TAG avec asset VirtualLib.dll — executer /publish prod d'abord"; exit 1; }
ARTEFACT="$DOWNLOAD_DIR/VirtualLib.dll"
test -f "$ARTEFACT" || { echo "Artefact non telecharge — abandon"; exit 1; }

MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media get pods -l app=emby
# → noter le nom du pod emby-XXXXXXX-XXXXX

cd "$REPO_ROOT"
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media cp "$ARTEFACT" <POD_NAME>:/config/plugins/VirtualLib.dll

MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media rollout restart deployment/emby
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media rollout status deployment/emby --timeout=60s
```

## Echec

```
SendMessage({
  to: "main",
  content: "DEPLOY FAILED
Environnement : PROD
Version  : v[X.Y.Z]
Etape    : Installation (kubectl cp / rollout restart)
Rollback : rollout undo deployment/emby"
})
```

## Rollback

```bash
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media rollout undo deployment/emby
```
