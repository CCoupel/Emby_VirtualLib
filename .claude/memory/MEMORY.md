# MEMORY.md — VirtualLib

> Source de vérité au démarrage de session. Mis à jour via `/end-session`.

---

## Version courante

- **Tag** : `v1.9.4` — `VirtualLib.csproj` aligné sur `1.9.4` (corrigé cette session, était à `1.9.2`)
- **Branche** : `main`
- **Build** : OK, 0 erreur (421 warnings, ~420 sont du CS1591 pré-existant — dette technique, pas bloquant)

---

## ⚠️ État critique — Tests cassés sur `main`

**`tests/VirtualLib.Tests` ne compile pas** — 3 erreurs `CS7036` dans `StrmGeneratorTests.cs` :
la signature de `StrmGenerator.Generate()` a évolué (paramètres obligatoires `libraryId`, `libraryName`, `virtualLibRoot` ajoutés — probablement lors du travail LibraryOrganization/v1.9.4), mais les tests n'ont pas été mis à jour.

- Détails : `tests/VirtualLib.Tests/StrmGeneratorTests.cs:29,52,71`
- Rapport QA complet (avant purge de `_work/`) : voir historique, ou relancer `dotnet test tests/VirtualLib.Tests/` pour régénérer
- **Pas encore corrigé** — décision utilisateur du 2026-08-04 : noter sans créer d'issue GitHub, à traiter à la prochaine session
- CI GitHub Actions probablement rouge tant que ce n'est pas corrigé

## Dette technique connue (non bloquante)

- `src/VirtualLib/Core/Cache/CacheManager.cs:840` — log `LogInformation` avec 5 placeholders nommés mais 4 arguments (`CA2017`) : `{ItemId}` ne se logue pas correctement (décalage positionnel, `NewEnd` reçoit la valeur de `ItemId`). Impact logs uniquement, pas de bug fonctionnel.
- ~420 warnings `CS1591` (commentaires XML manquants sur membres publics) dans tout le projet — pré-existant.

---

## Travail en cours / contexte de session (2026-08-04)

- `/init-project` exécuté pour la première fois : template `CCoupel/claude_project_template` (commit `501e73d`) déployé, `project-config.json` créé, agent `dev-plugin.md` adapté au projet (C#/Emby), CLAUDE.md fusionné (contenu existant préservé + sections template + protocole Teamleader).
- 56 fichiers avaient une conversion parasite LF→CRLF (aucun contenu réel) — restaurés en LF. Seuls `.gitignore`, `CLAUDE.md` et `.claude/settings.local.json` avaient de vrais changements.
- `doc-updater` a audité et mis à jour la doc : `CHANGELOG.md` créé (historique complet v1.0.0→v1.9.4), version `.csproj` corrigée, `CLAUDE.md` phase actuelle mise à jour, `docs/core/SYNC.md` complété (section parallélisation v1.9.4).
- Environnement : le SDK .NET n'est PAS dans `Program Files` (runtime seulement) — il faut utiliser `/mnt/c/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe` (ou l'équivalent Windows) depuis WSL. Documenté dans `.claude/commands/build.md`/`test.md`, mais les agents l'oublient parfois — le rappeler si besoin.

## Décisions techniques prises cette session

- Modèle multi-agent adapté au harnais réel : pas de `TeamCreate`/IDLE littéral, mais spawn une fois (le teammate lit son protocole + rôle, ACK, termine son tour) puis dispatch ultérieur via `SendMessage` qui le réveille avec le contexte déjà chargé — fonctionne via le système de mailbox.
- CLAUDE.md : stratégie de fusion (garder le contenu projet existant, ajouter les sections template à la fin) plutôt que remplacement complet.
- Nom canonique `deployer` (table CLAUDE.md) ≠ `subagent_type: deploy` (agent réellement enregistré) — à garder en tête lors du spawn (utiliser `name: "deployer", subagent_type: "deploy"`).

## Issues GitHub ouvertes (8, aucune sur milestone actif)

#12, #14, #15 (phase-2/3), #23, #24, #26, #27, #28 (catalog-compliance) — voir `gh issue list` pour le détail à jour.

## Pour la prochaine session

1. **Corriger `StrmGeneratorTests.cs`** (signature `Generate()`) avant de pouvoir à nouveau exécuter la suite de tests.
2. Vérifier si d'autres fichiers de tests utilisent l'ancienne signature de `StrmGenerator.Generate` (QA n'a pu confirmer que `StrmGeneratorTests.cs`, la compilation s'arrête à la première erreur).
3. Envisager de traiter le warning `CA2017` dans `CacheManager.cs:840`.
4. Aucun milestone actif — envisager d'en créer un si un cycle de release est prévu.
