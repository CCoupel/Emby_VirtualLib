# MEMORY.md — VirtualLib

> Source de vérité au démarrage de session. Mis à jour via `/end-session`.

---

## Version courante

- **Dev** : `v1.10.0.7` (schema X.Y.Z.a for dev branch, HEAD `1f69df5`)
- **Prod target** : `v1.11.0.0` (milestone, schema X.Y.Z.0 for releases)
- **Branche** : `main` (26+ commits ahead of `origin/main`, no push this session)
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

## Issues GitHub ouvertes

**Milestone v1.11.0** (6 issues, #44 just closed):
- #12 (Orphan cleanup shared brick) — READY FOR DEV
- #24 (Catalog version compliance) — ACTIVE (schema implemented, validation pending)
- #26 (Production logs visible) — PARTIALLY RESOLVED (#44 feat, may need more instrumentation)
- #45 (root cause identified, fix ready) — READY FOR DEV
- #46 (cache-busting manual) — READY FOR DEV

**Backlog** (8 autres issues):
#14, #15 (phase-2/3), #23, #27, #28 (catalog-compliance) — voir `gh issue list` pour le détail à jour.

## Priorités prochaines sessions

### Immédiat (ready-to-start)
1. **#45** — Root cause déjà identifiée par dev-plugin cette session, fix prêt à lancer
2. **#46** — Cache-busting manuel, directement lié à la saga checkbox qu'on vient de résoudre (configuration UI refresh strategy)

### Court terme (après #44 promotion en v1.11.0.0)
3. **#12** — Orphan cleanup (shared brick, dry-run now; needs full deletion impl + safety testing)
4. **#24** — Catalog version compliance (schema applied, further catalog-side validation pending from Emby/Jellyfin)
5. **#26** — Production log visibility (partially resolved in #44; may need further instrumentation for specific connectors)

### Attention particulière
- **26+ commits not pushed to `origin/main`** — current session strategy; décision needed : push now, or batch with next release?
- Warning `CA2017` in `CacheManager.cs:840` (log placeholder mismatch) — low priority, logs only
- ~420 warnings `CS1591` (missing XML docs) — pre-existing, acceptable as technical debt
