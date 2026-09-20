# Deploy PROD — Adaptations VirtualLib

> Compagnon de `deploy.prod.template.md` — meme mecanisme que `deploy.qualif.md` (copie directe
> du `.dll` via `kubectl cp`, pas de Helm), transpose au deployment `emby` (production).
>
> **⚠ NON VALIDE EN CONDITIONS REELLES** — contrairement a la procedure QUALIF (testee sur de
> multiples cycles, voir `.claude/memory/MEMORY.md`), cette procedure PROD est deduite par
> analogie depuis QUALIF (CLAUDE.md mentionne `kubectl → emby2/emby, namespace media` mais aucun
> deploiement PROD via ce mecanisme n'a encore ete trace). A verifier/confirmer manuellement
> avant le premier `/deploy prod` reel.

> **Namespace** : `media`. Kubeconfig : `private/kubeconfig.yml`. Deployment cible : `emby`
> (jamais `emby2`, qui est QUALIF).

```bash
REPO_ROOT=$(git rev-parse --show-toplevel)
ARTEFACT="$REPO_ROOT/build/qualif_v$DIR_VERSION/VirtualLib.dll"   # publie par PUBLISH PROD
test -f "$ARTEFACT" || { echo "Aucun artefact publie — executer /publish prod d'abord"; exit 1; }

MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media get pods -l app=emby
# → noter le nom du pod emby-XXXXXXX-XXXXX

cd "$REPO_ROOT"
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media cp "build/qualif_v$DIR_VERSION/VirtualLib.dll" <POD_NAME>:/config/plugins/VirtualLib.dll

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
