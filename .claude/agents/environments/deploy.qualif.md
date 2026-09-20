# Deploy QUALIF — Adaptations VirtualLib

> Compagnon de `deploy.qualif.template.md` — remplace le mecanisme generique Helm/image-tag du
> template (non applicable ici) par la procedure reelle et validee : copie directe du `.dll`
> compile dans le pod Emby via `kubectl cp`, puis restart du deployment. Pas de Helm, pas de
> registre d'images — le plugin se charge au demarrage d'Emby depuis `/config/plugins/`.

> **Toujours deployer sur `emby2`, jamais sur `emby`** (emby = production, voir `deploy.prod.md`).
> Namespace : `media`. Kubeconfig : `private/kubeconfig.yml`.

## Variables attendues

Aucune variable d'environnement — kubeconfig et namespace sont fixes pour ce projet (voir
ci-dessus), pas de secret externe a charger.

```bash
# 1. Verification — artefact publie par PUBLISH QUALIF
REPO_ROOT=$(git rev-parse --show-toplevel)
ARTEFACT="$REPO_ROOT/build/qualif_v$DIR_VERSION/VirtualLib.dll"
test -f "$ARTEFACT" || { echo "Aucun artefact publie — executer /publish qualif d'abord"; exit 1; }

# 2. Trouver le pod emby2 courant
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media get pods -l app=emby2
# → noter le nom du pod emby2-XXXXXXX-XXXXX

# 3. Copier le DLL dans le pod (chemin source relatif — un chemin Windows absolu casse kubectl
# sous Git Bash, qui convertit les /c/... )
cd "$REPO_ROOT"
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media cp "build/qualif_v$DIR_VERSION/VirtualLib.dll" <POD_NAME>:/config/plugins/VirtualLib.dll

# 4. Redemarrer le deployment
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media rollout restart deployment/emby2
ROLLOUT_STATUS_CHECK=$(MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media rollout status deployment/emby2 --timeout=60s; echo $?)

# 5. Verifier le DLL dans le nouveau pod
NOUVEAU_POD=$(MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media get pods -l app=emby2 -o jsonpath='{.items[0].metadata.name}')
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media exec "$NOUVEAU_POD" -- ls -la /config/plugins/VirtualLib.dll
```

**ROLLOUT_STATUS_CHECK = 0 → continuer vers Notification.**

**ROLLOUT_STATUS_CHECK ≠ 0 → executer le protocole d'echec ci-dessous.**

## Echec

```
SendMessage({
  to: "main",
  content: "DEPLOY FAILED
Environnement : QUALIF
Version  : v[X.Y.Z]
Etape    : Installation (kubectl cp / rollout restart)
Rollback : rollout undo deployment/emby2"
})
```

## Rollback

```bash
MSYS_NO_PATHCONV=1 kubectl --kubeconfig "$REPO_ROOT/private/kubeconfig.yml" \
  -n media rollout undo deployment/emby2
```

## Notes

- Le plugin se charge au demarrage d'Emby — un restart est obligatoire apres chaque deploiement.
- Apres deploiement, relancer une sync depuis l'UI pour regenerer les fichiers `.strm`.
