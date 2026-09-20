# Commande /build

Construire une version candidate — agnostique a l'environnement, la seule etape ou du code est
compile. Voir `agents/deploy.md` Tache BUILD.

## Usage

```
/build
```

## Argument recu

$ARGUMENTS

## Mots-cles de controle

**Reference :** Voir `context/COMMON.md` section 12

| Mot-cle | Action |
|---------|--------|
| `help` | Affiche l'aide et les mots-cles disponibles |
| `status` | Affiche l'etat du workflow en cours |
| `plan` | Affiche le plan sans executer |
| `resume <phase>` | Reprend a une phase |
| `skip <phase>` | Saute une phase |
| `jumpto <tache>` | Demarre a une tache precise du plan |

Si `$ARGUMENTS` commence par un mot-cle -> executer l'action correspondante.
Sinon -> workflow normal.

## Principe

`/build` ne prend pas d'environnement en argument : c'est la seule etape ou l'artefact est
compile. Le candidat resultant (tague `X.Y.Z.a`) est ensuite rendu disponible tel quel pour
QUALIF par `/publish qualif` (promotion, zero rebuild), puis, apres validation, republie de
maniere deterministe pour PROD par `/publish prod` (rebuild via CI depuis la meme source
figee) — principe BORE, voir `agents/infra.md` section 3.

## Prerequis

- [ ] Tests QA passes
- [ ] Revue de code approuvee

## Workflow

```
/build
    |
    v
Verification --> Increment version (a) --> Compilation --> Notification
```

## Exemples

```
/build    # Verification + compilation de la version candidate courante
```

## Agent

`/build` dispatch direct au teammate `deployer` (en IDLE depuis `/start-session`) :
`SendMessage({to: "deployer", content: "BUILD"})`

En orchestration CDP, `/build` n'est jamais invoque seul : la Phase 5 (QUALIF) du CDP
dispatch au `deployer` un ordre chaine `BUILD` puis `PUBLISH QUALIF` puis `DEPLOY QUALIF`,
execute en sequence par l'agent avant de repondre. Voir `agents/cdp.md` Phase 5 pour le
protocole complet.

Spec : `.claude/agents/deploy.md` (voir Tache BUILD)
