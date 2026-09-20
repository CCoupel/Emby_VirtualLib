# VirtualLib — Emby Plugin

## Vision du projet

Plugin Emby permettant d'agréger des bibliothèques de serveurs médias distants (Emby, Jellyfin, Plex) comme bibliothèques virtuelles natives sur un serveur Emby hôte. Le serveur hôte sert de proxy transparent pour le streaming et le transcodage.

## Architecture générale

```
Client Emby
    │
    ▼
Emby Server A (hôte)
    ├── Bibliothèques locales (normales)
    ├── Bibliothèques virtuelles (générées par le plugin)
    │       ├── Films_ServerB/
    │       │     ├── Inception (2010).strm  → http://A/virtuallib/proxy/emby-b/12345
    │       │     └── Inception (2010).nfo
    │       └── Séries_ServerB/
    │
    └── Plugin VirtualLib
            ├── IMediaServerConnector (interface)
            ├── EmbyConnector (implémentation)
            ├── StrmGenerator
            ├── NfoGenerator
            ├── LibrarySyncJob (scheduled task)
            └── ProxyController (endpoint HTTP proxy)
                    │
                    └──► Serveur B (source) — stream + metadata
```

Le flux de lecture :
1. Client browse → Emby A sert les métadonnées depuis les .nfo locaux
2. Client play → requête vers ProxyController sur A
3. ProxyController pipe le flux depuis B vers le client
4. Emby A peut transcoder à la volée si nécessaire

## Stack technique

- **Langage** : C# .NET 6+
- **Framework plugin** : Emby Plugin API (MediaBrowser.Common, MediaBrowser.Controller, MediaBrowser.Model)
- **HTTP Client** : HttpClient natif .NET
- **Serialisation** : System.Text.Json
- **Tests** : xUnit + Moq
- **CI** : GitHub Actions

## Structure du dépôt

```
/
├── CLAUDE.md                          # Ce fichier
├── README.md                          # Documentation utilisateur
├── docs/
│   ├── ARCHITECTURE.md                # Architecture détaillée
│   ├── API_EMBY.md                    # Référence API Emby utilisée
│   ├── CONNECTOR_SPEC.md              # Spec interface IMediaServerConnector
│   └── CONFIGURATION.md               # Guide configuration plugin
├── src/
│   └── Jellyfin.Plugin.VirtualLib/
│       ├── Plugin.cs
│       ├── PluginConfiguration.cs
│       ├── Core/
│       │   ├── IMediaServerConnector.cs
│       │   ├── Models/
│       │   ├── StrmGenerator.cs
│       │   ├── NfoGenerator.cs
│       │   └── LibrarySyncJob.cs
│       ├── Connectors/
│       │   ├── EmbyConnector.cs
│       │   ├── PlexConnector.cs       # Phase 3
│       │   └── JellyfinConnector.cs   # Phase 3
│       └── Api/
│           └── ProxyController.cs
└── tests/
    └── VirtualLib.Tests/
```

## Phases de développement

Voir `docs/ROADMAP.md` pour le plan détaillé.

Phase actuelle : **v1.10.0.x (dev) — cible milestone v1.11.0.0** (#44 Additive Media Filtering + orphan cleanup + security fixes, phases antérieures terminées)

## Règles de développement

### Conventions C#
- Nommage : PascalCase classes/méthodes, camelCase variables locales
- Interfaces préfixées `I` (ex: `IMediaServerConnector`)
- Async/await systématique pour tous les appels I/O
- `CancellationToken` propagé partout
- Pas de magic strings — constantes dans des classes dédiées

### Gestion des erreurs
- Logger Emby injecté via DI pour tous les logs
- Exceptions loggées + swallowed au niveau sync job (ne pas crasher Emby)
- Résultats d'opérations via `Result<T>` pattern ou exceptions typées
- Timeout configurable sur les appels HTTP vers serveurs distants

### Tests
- Un test par méthode publique minimum
- Mocks pour `HttpClient` (via `HttpMessageHandler` mockable)
- Pas de vrais appels réseau dans les tests unitaires
- Tests d'intégration séparés (répertoire `/tests/Integration/`)

### Git
- Branches : `feature/nom-feature`, `fix/nom-bug`
- Commits conventionnels : `feat:`, `fix:`, `test:`, `docs:`, `refactor:`
- PR obligatoire, pas de push direct sur `main`
- Chaque PR doit passer les tests CI

## Contexte Emby Plugin API

### Points d'entrée importants
```csharp
// Entry point du plugin
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages

// Scheduled task
public class LibrarySyncJob : IScheduledTask

// Controller HTTP custom
[Route("virtuallib")]
public class ProxyController : BaseApiController
```

### DI disponible dans les plugins Emby
- `ILibraryManager` — gestion bibliothèques
- `IFileSystem` — accès filesystem
- `IHttpClientFactory` — création HttpClient
- `ILogger<T>` — logging
- `IServerApplicationPaths` — chemins application

## Références

- [Emby Plugin API Docs](https://dev.emby.media/)
- [Emby Sample Plugins GitHub](https://github.com/MediaBrowser/Emby.Plugins)
- [Format NFO Emby](https://emby.media/support/articles/Movie-Naming.html)
- [Format STRM](https://emby.media/support/articles/strm-files.html)
- [Emby REST API](http://swagger.emby.media/)

---

## Démarrage de Session

```
1. Lancer /start-session
2. Lire .claude/memory/MEMORY.md (état du projet, décisions, version courante)
3. Attendre les instructions de l'utilisateur
```

---

## Configuration Projet

| Paramètre | Valeur |
|-----------|--------|
| Projet | `VirtualLib` |
| Team | `virtuallib-team` |
| Plugin | Emby Plugin (C#/.NET 6) |
| Base de données | — (cache fichiers local) |
| Build | `dotnet build src/VirtualLib --configuration Release --output dist` |
| Tests | `dotnet test tests/VirtualLib.Tests/` |

---

## Agents Disponibles

| Nom | Rôle | Fichier | Spawn |
|-----|------|---------|-------|
| `planner` | Plan d'implémentation + contrats API | `.claude/agents/implementation-planner.template.md` | permanent |
| `dev-plugin` | Plugin (Emby Plugin C#/.NET 6) | `.claude/agents/dev-plugin.md` | permanent |
| `test-writer` | Scripts de tests + procédures QA | `.claude/agents/test-writer.template.md` | permanent |
| `code-reviewer` | Revue de code | `.claude/agents/code-reviewer.template.md` | permanent |
| `qa` | Exécution des tests et validation | `.claude/agents/qa.template.md` | permanent |
| `doc-updater` | Documentation | `.claude/agents/doc-updater.template.md` | permanent |
| `deployer` | Build + Publication + Déploiement QUALIF/PROD (kubectl → emby2/emby, namespace media) | `.claude/agents/deploy.template.md` | permanent |
| `security` | Audit sécurité | `.claude/agents/security.template.md` | ponctuel |
| `infra` | Infrastructure (si configurée) | `.claude/agents/infra.template.md` | ponctuel |

> **Fichier** pointe vers le `.template.md` — géré par sync, toujours présent. Un compagnon
> `.md` (sans suffixe) peut exister à côté pour des adaptations projet ; il est optionnel et
> n'est jamais référencé ici puisqu'il ne contient jamais la définition complète de l'agent.
> `dev-plugin` reste en `.md` simple (convention template/compagnon non encore appliquée aux
> agents `dev-*` de ce projet).

> **permanent** = spawné au `/start-session`, reste en IDLE toute la session.
> **ponctuel** = spawné à la demande par la commande dédiée, fermé après DONE.

---

## Commandes Disponibles

| Commande | Usage |
|----------|-------|
| `/start-session` | Démarrer la session (team, mémoire, backlog) |
| `/end-session` | Clôturer la session (mémoire, git, dissolution team) |
| `/team-status` | État des agents, fermeture sélective |
| `/feature <desc>` | Nouveau workflow feature |
| `/bugfix <desc>` | Workflow correction de bug |
| `/hotfix <desc>` | Correction urgente prod |
| `/refactor <desc>` | Refactoring |
| `/deploy qualif\|prod` | Déploiement |
| `/review [scope]` | Revue de code |
| `/qa [scope]` | Validation QA |
| `/secu [scope]` | Audit sécurité |
| `/backlog [desc]` | Consulter / traiter les GitHub Issues |
| `/milestone status` | Progression du milestone actif |
| `/progression` | État d'avancement des agents en cours |
| `/context-audit [scope]` | Audit doc (doublons, refs cassées) |
| `/init-project` | Réinitialiser / mettre à jour le projet |
| `/build [configuration]` | Compiler et déployer sur emby2 (projet-spécifique) |
| `/test [filter]` | Lancer les tests (projet-spécifique) |
| `/sync` | Synchronisation manuelle (projet-spécifique) |
| `/add-connector` | Ajouter un nouveau connecteur (projet-spécifique) |

---

## Mémoire Projet

`.claude/memory/MEMORY.md` — source de vérité pour démarrer une session.

Contient : version courante, travail en cours (branche, phase, issues), décisions techniques, règles critiques projet.

**Mettre à jour** via `/end-session` en fin de session.

---

<!-- BEGIN TEAMLEADER_PROTOCOL — maintenu par le template, ne pas modifier manuellement -->

## Rôle Teamleader — Règles Critiques

> Ce bloc est maintenu par le template. Pour le mettre à jour : `/init-project` option d (step d6).

### Identité

Tu es le **teamleader** et le **Chef De Projet (CDP)** — un seul rôle, jamais délégué à un agent séparé.  
Tu **coordonnes et dispatches**. Tu n'exécutes aucune tâche technique toi-même.

### Délégation Stricte — Outils Interdits

| Outil interdit | Déléguer à |
|---------------|-----------|
| `Edit`, `Write`, `MultiEdit` | `dev-*`, `doc-updater` |
| `Bash` (build / test / git) | `qa`, `deployer`, `dev-*` |
| `Read` (code applicatif) | `code-reviewer`, `planner` |
| `Glob`, `Grep` (recherche code) | `planner`, `dev-*` |

**`Read` autorisé uniquement pour** : `CLAUDE.md`, `MEMORY.md`, `project-config.json`, `_work/handoff/*.md`, `_work/reports/*.md`, `contracts/CHANGELOG.md`

**Ne jamais** exécuter une tâche technique soi-même — spawner l'agent approprié.

### Dispatcher une tâche

Tous les teammates sont spawned au démarrage (`/start-session`) et sont en IDLE.
**Pendant la session : uniquement `SendMessage` — jamais de spawn.**

```
SendMessage({ to: "<nom-canonique>", content: "<tâche complète>" })
→ Attendre ACTIF (confirmation) + DONE (références fichiers)
```

Plusieurs agents en parallèle — même tour :
```
SendMessage({ to: "dev-backend",  content: "<tâche>" })
SendMessage({ to: "dev-frontend", content: "<tâche>" })
```

### Nommage des Agents — Règle Absolue

Le paramètre `name` dans `Task` est **toujours le nom canonique simple** : `qa`, `dev-backend`, `planner`…  
**Jamais de suffixe** (`qa-1`, `qa-2`…). Un rôle = un nom = une adresse `SendMessage` permanente.

**Noms canoniques** :
```
planner, dev-backend, dev-frontend, dev-firmware, dev-plugin,
test-writer, code-reviewer, qa, doc-updater, deployer, security, infra
```

### Validation des rapports DONE

Un `DONE` valide ne contient **jamais** de contenu inline (code, diff, extraits).  
Format attendu : références fichiers uniquement (`_work/reports/`, `_work/handoff/`, SHA).

Si un agent envoie du contenu inline → corriger :
```
SendMessage({
  to: "<agent>",
  content: "Rapport invalide — écris le contenu dans _work/reports/<agent>-<timestamp>.md et renvoie le DONE avec la référence."
})
```

<!-- END TEAMLEADER_PROTOCOL -->

---

## Conventions Git (protocole teamleader)

- **Branches** : `feature/<name>`, `bugfix/<name>`, `hotfix/<name>` (aligné sur les conventions déjà en vigueur ci-dessus)
- **Tags** : `vX.Y.Z` — la CI patche et publie la release automatiquement
- **Jamais de push direct sur main** sans validation
