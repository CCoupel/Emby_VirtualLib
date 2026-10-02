# MEMORY.md — VirtualLib

> Source de vérité au démarrage de session. Mis à jour via `/end-session`.

---

## Version courante

- **Prod** : `v1.11.0.0` — ✅ **LIVRÉ en PROD (emby)**, release GitHub publiée, milestone FERMÉ
- **Dev** : `v1.12.0.x` (prochain cycle vers v1.13.0 prod, schema X.Y.Z.a)
- **Branche** : `main` (commits synced avec `origin/main` post-release)
- **Build** : OK, 0 erreur (421 warnings, ~420 sont du CS1591 pré-existant — dette technique, pas bloquant)

---

## État des tests

**`tests/VirtualLib.Tests`** — ✅ **Résolu cette session**

- Issue antérieure (2026-08-04) : 3 erreurs `CS7036` dans `StrmGeneratorTests.cs` (signature `Generate()` incompatible avec tests)
- **Status courant** : 115/115 tests PASS (validé QA à plusieurs reprises cette session)
- Build sans erreur, tests d'intégrité complète OK

## Dette technique connue (non bloquante)

- `src/VirtualLib/Core/Cache/CacheManager.cs:840` — log `LogInformation` avec 5 placeholders nommés mais 4 arguments (`CA2017`) : `{ItemId}` ne se logue pas correctement (décalage positionnel, `NewEnd` reçoit la valeur de `ItemId`). Impact logs uniquement, pas de bug fonctionnel.
- ~420 warnings `CS1591` (commentaires XML manquants sur membres publics) dans tout le projet — pré-existant.

---

## Session 2026-10-02 (sync template + conformité CI/infra)

- Template synchronisé **v3.8.0 → v3.9.0** (`template_version` dans project-config.json). Commandes projet `add-connector`, `sync`, `test` conservées (ce ne sont PAS des reliquats).
- `project-config.json` : `version_file` = `.claude/project-config.json` (champ `"version"`, miroir `<Version>` dans `src/VirtualLib/VirtualLib.csproj`, bumpés ensemble par `deployer`), `src_dir` = `src/VirtualLib`. Avant : null → placeholders vides dans les fichiers déployés.
- **Version PROD = X.Y.Z.0 (4 segments)**, tag `vX.Y.Z.0`. Dérogation au template (qui coupe à 3 segments) dans `.claude/agents/environments/publish.prod.md` (nouveau) et `.claude/agents/deploy.md` (lecture/écriture version via `jq` + sed sur le csproj).
- `.github/workflows/release.yml` : ajout de `dotnet test tests/VirtualLib.Tests/` avant le build — **jamais exécuté en CI** (se déclenche uniquement sur tag) ; à surveiller à la prochaine release.
- Commits poussés sur main : `23bfede`, `5620a60`.
- Restes : un `git stash` ancien (WIP sur f2e7393) ; fichiers non suivis `null`, `tmp/`, `dist/`, `docs/releases/`, 2 captures dans `tests/`.

## Travail complété cette session (2026-08-04 → 2026-08-07)

### PR #44 — Additive Media Filtering + Enhancements (26 commits, 115/115 tests PASS)
- **Feature livrée** : Filtrage additif des médias distants par résolution minimale, langues audio/sous-titres, avec mode dry-run pour cleanup des orphelins
- **Améliorations connexes** : Compteur live 3 valeurs (Items/Total/Sync), stream capture complète Emby/Plex, injection des pistes audio/sous-titres dans NFO et MediaStreams
- **Fixes de sécurité** : Protection symlink cleanup, mutex synchronisation connecteur, logs production visibles
- **Corrections de configuration** : `CacheEnabled` persistence, `toggleLibrary` préservation `LocalUserId`
- **Saga checkbox cache** (4 rounds d'investigation) : Root cause identificd = incompatibilité classe CSS `emby-checkbox` (exige sibling `checkboxLabel` absent du markup); fix final = retrait classe sur 3 checkboxes, sacrifice style visuel Emby pour rendu natif fiable
- **Déploiements QUALIF** : Multiples cycles validation emby2, tous les bugs résolus et confirmés par utilisateur
- ✅ **Prêt pour promotion en v1.11.0.0**

### Version Schema Decision
- **Adopté** : X.Y.Z.a pour dev (a = suffixe, ex. 1.10.0.1 → 1.10.0.7), X.Y.Z.0 pour prod (ex. 1.11.0.0)
- **Rationale** : Emby catalog exige 4 segments de version; prod demande .0 pour stabilité; dev iterates avec lettre/chiffre
- **Implémentation** : Plugin.cs, project-config.json, CHANGELOG.md, contracts/CHANGELOG.md, CLAUDE.md alignés

### Documentation Updates
- CHANGELOG.md : 1.11.0.0 release entry + detailed checkbox saga documentation
- contracts/CHANGELOG.md : Milestone refs realigned to v1.11.0
- CLAUDE.md : Phase actuelle updated to v1.10.0.x (dev) — cible v1.11.0.0
- MEMORY.md (today) : Version + test status + session work summary

## Décisions techniques

### Session 2026-08-04 (init-project)
- Modèle multi-agent adapté au harnais réel : pas de `TeamCreate`/IDLE littéral, mais spawn une fois (le teammate lit son protocole + rôle, ACK, termine son tour) puis dispatch ultérieur via `SendMessage` qui le réveille avec le contexte déjà chargé — fonctionne via le système de mailbox.
- CLAUDE.md : stratégie de fusion (garder le contenu projet existant, ajouter les sections template à la fin) plutôt que remplacement complet.
- Nom canonique `deployer` (table CLAUDE.md) ≠ `subagent_type: deploy` (agent réellement enregistré) — à garder en tête lors du spawn (utiliser `name: "deployer", subagent_type: "deploy"`).
- Environnement WSL : SDK .NET accessible via `/mnt/c/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe` (pas dans Program Files).

### Session 2026-08-06 (Version Schema + #44 Validation)
- **Versioning Schema** (issue #24 catalog compliance) : X.Y.Z.a pour dev (suffixe itératif), X.Y.Z.0 pour prod. Emby catalog exige 4 segments; prod stable sur .0.
- **GitHub Milestone Management** : Anticiper milestone target lors du développement (1.11.0 dès le démarrage de #44, même en dev 1.10.0.x)

## Issues GitHub — Milestone Management

**Milestone v1.11.0** — ✅ **CLOSED (release livrée v1.11.0.0)**
- #44: Closed (filtrage additif + compteurs + checkbox fix)
- Closed at: 2026-08-07T08:25:00Z
- Status: Complete, production validated

**Milestone v1.13.0** (5 issues, next production cycle):
- #12 (Orphan cleanup shared brick) — READY FOR DEV (dry-run done, needs full deletion + safety)
- #24 (Catalog version compliance) — ACTIVE (schema implemented, CI supports 4-part tags since 3217479, issue remains open)
- #26 (Production logs visible) — PARTIALLY RESOLVED (#44 feat, may need more instrumentation)
- #45 (NFO providers ILogger<T> DI registration) — **READY FOR DEV** (root cause identified, fix ready in dev-plugin)
- #46 (Cache-busting configjs.js automatic) — READY FOR DEV (linked to resolved checkbox saga)

**Backlog** (8 autres issues):
#14, #15 (phase-2/3), #23, #27, #28 (catalog-compliance) — voir `gh issue list` pour le détail à jour.

## Priorités cycle v1.13.0 (prochaines sessions)

### Immédiat (ready-to-start)
1. **#45** (ILogger<T> DI registration for NFO providers) — ⭐ **Root cause identified, fix ready in dev-plugin** — Launch first
2. **#46** (Cache-busting configjs.js automatic) — Linked to checkbox saga resolution, UI refresh strategy optimization

### Court terme (v1.13.0)
3. **#12** (Orphan cleanup shared brick) — Needs full deletion implementation + safety testing (dry-run exists)
4. **#24** (Catalog version compliance) — Schema applied, CI/CD supports 4-part tags; issue remains for catalog-side validation (Emby/Jellyfin teams)
5. **#26** (Production log visibility) — Partially resolved in #44; may need further instrumentation for specific connectors

### Notes de suivi
- v1.11.0.0 production release ✅ completed and validated on `emby`
- Milestone v1.11.0 ✅ closed (6 issues total including #44)
- Milestone v1.13.0 ✅ created with 5 issues ready for next cycle
- GitHub milestones convention: **Y even = dev version, Y odd = prod milestone target**
  - v1.12.0.x (dev) → v1.13.0 (prod milestone)
- Warning `CA2017` in `CacheManager.cs:840` (log placeholder) — low priority
- ~420 warnings `CS1591` (XML docs) — pre-existing technical debt
