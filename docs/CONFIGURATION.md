# Guide de configuration — VirtualLib

## Documentation technique

- Architecture → [core/OVERVIEW.md](./core/OVERVIEW.md)
- Synchronisation & NFO → [core/SYNC.md](./core/SYNC.md)
- Proxy streaming → [core/PROXY.md](./core/PROXY.md)
- EmbyConnector → [connectors/EMBY.md](./connectors/EMBY.md)
- PlexConnector → [connectors/PLEX.md](./connectors/PLEX.md)

---

## Installation du plugin

### Méthode 1 : Via le catalogue de plugins Emby
*(Disponible après publication)*
Dashboard → Plugins → Catalogue → VirtualLib → Installer

### Méthode 2 : Installation manuelle
1. Télécharger `VirtualLib.dll` depuis les releases GitHub
2. Copier dans `{EmbyData}/plugins/` (fichier à la racine, pas dans un sous-dossier)
3. Redémarrer Emby Server

### Méthode 3 : Déploiement sur pod Kubernetes

```bash
# Build du plugin
"C:/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe" build \
  src/VirtualLib/ --configuration Release --output ./dist/

# Copie dans le pod (MSYS_NO_PATHCONV=1 requis pour éviter la conversion de chemin Git Bash)
MSYS_NO_PATHCONV=1 kubectl cp dist/VirtualLib.dll \
  media/emby2-<pod-id>:/config/plugins/VirtualLib.dll \
  --kubeconfig=/c/Users/cyril/.kube/config

# Redémarrage du pod
kubectl rollout restart deployment/emby2 -n media
```

**Notes Kubernetes** :
- Le plugin se place à `/config/plugins/VirtualLib.dll` (plat, pas de sous-dossier)
- Pour éviter les problèmes de hairpin NAT (pod → DNS externe → même cluster), utiliser des `hostAliases` sur le déploiement Emby pour forcer la résolution interne

---

## Configuration initiale

### Étape 1 : Ouvrir la configuration du plugin
Dashboard → Plugins → VirtualLib → Paramètres

### Étape 2 : Définir le répertoire des bibliothèques virtuelles

```
Chemin racine des bibliothèques virtuelles : /media/virtual-libraries
```

Ce répertoire sera créé automatiquement par le plugin. Il doit être accessible en écriture par le processus Emby.

### Étape 3 : Ajouter un serveur source

Cliquer **"+ Add Connector"** et remplir :

| Champ | Exemple | Description |
|---|---|---|
| Nom d'affichage | `Serveur B - Bureau` | Nom libre pour identifier la source |
| Type de serveur | `Emby` | Emby / Jellyfin / Plex / Plex via plex.tv |
| URL du serveur | `http://192.168.1.200:8096` ou `https://media.example.com/emby` | URL complète avec port **et chemin de base** Emby (non requis pour Plex via plex.tv) |
| Mode d'authentification | `User Credentials` | Voir section Authentication ci-dessous |
| Mode de métadonnées | `Remote Sync` | Voir section Metadata Source ci-dessous |
| Bibliothèques parallèles max | `4` | Nombre de bibliothèques synchées simultanément pour ce connecteur (Phase 1 uniquement) |

**Important — URL du serveur** : inclure le chemin de base si Emby est derrière un reverse proxy.
Exemple : si Emby répond sur `https://media.example.com/emby/Items/...`, l'URL à configurer est `https://media.example.com/emby`.

Cliquer **"Test Connection"** pour valider avant de sauvegarder.

#### Authentification

| Mode | Description |
|---|---|
| **API Key** | Accès admin complet — les états utilisateur (lu/favori/position) remontés sont ceux de l'admin, souvent tous à zéro si l'admin ne regarde pas lui-même les médias |
| **User Credentials** | Le serveur distant voit les sessions de cet utilisateur — **recommandé** pour que la synchronisation des états (lu, favori, position de reprise) reflète les données réelles de l'utilisateur |

> **Recommandation** : utiliser **User Credentials** si vous souhaitez que les états lu/favori/position soient correctement synchronisés depuis le serveur source.

#### Metadata Source

| Mode | Description |
|---|---|
| **Remote Sync** | Le plugin télécharge métadonnées et images depuis le serveur distant. Items avec `.nfo` existant ignorés (incrémental). |
| **Remote Sync Full** | Identique à Remote Sync mais réécrit tous les `.nfo` et re-télécharge les images à chaque sync. |
| **Local Scraping** | Seuls les `.strm` sont créés. Emby utilise ses propres scrapers (TMDB, TVDB, FanArt) pour enrichir la bibliothèque. Nécessite un accès internet. |

### Étape 4 : Sélectionner les bibliothèques à synchroniser

Après un test de connexion réussi, la liste des bibliothèques disponibles s'affiche. Cocher celles à inclure — les dossiers virtuels Emby sont créés automatiquement.

### Étape 5 : Configurer la synchronisation automatique

```
Intervalle de synchronisation : 6 heures  (recommandé)
Timeout proxy stream :          30 secondes
```

### Étape 6 : Lancer la première synchronisation

Cliquer **"Synchronise Now"** pour déclencher la première sync manuellement. Toutes les bibliothèques activées sont synchronisées en parallèle. L'avancement est affiché en temps réel dans l'arbre des connecteurs :
- **Barre bleue** : Phase 1 (génération des `.strm` / `.nfo` / artwork)
- **Barre verte** : Phase 2 (injection des métadonnées Emby post-scan)
- Les deux barres partagent la même échelle (total d'items en Phase 1)

Les dossiers virtuels Emby sont créés automatiquement par le plugin — aucune configuration manuelle dans le Dashboard Emby n'est nécessaire.

Les médias du serveur distant apparaissent dans l'interface d'Emby après le scan de bibliothèque déclenché automatiquement en fin de sync.

---

## Connecteur Plex

### Plex (IP directe)

Utiliser le type **Plex (direct IP:32400)** avec :
- URL : `http://192.168.1.10:32400`
- Clé API : token X-Plex-Token de votre compte Plex (Settings → Account → Token)

### Plex via plex.tv

Utiliser le type **Plex via plex.tv (relay / plex.direct)** :
1. Laisser le champ URL vide
2. Saisir votre email/mot de passe plex.tv dans les champs d'authentification
3. Si la double authentification (2FA) est activée sur votre compte, entrer le code TOTP dans le champ prévu
4. Cliquer **"Load Servers from plex.tv"** — la liste de vos serveurs Plex apparaît
5. Sélectionner le serveur cible dans la liste déroulante

Le plugin résout automatiquement la meilleure URL disponible (locale → plex.direct → relay).
Le token d'accès est stocké dans la configuration du plugin et réutilisé pour les syncs suivantes.

**Note relay** : les connexions relay Plex peuvent être lentes (latence 30 s+). Le timeout est configuré à 120 s.

---

## Obtenir la clé API d'un serveur Emby/Jellyfin

### Emby
Dashboard → Avancé → Clés API → Nouvelle clé API

### Jellyfin
Dashboard → Avancé → Clés API → + (Ajouter)

---

## Paramètres avancés

### Chemin personnalisé par bibliothèque

Par défaut, le plugin crée :
```
{racine}/{NomDuServeur}_{NomDeLaBibliothèque}/
```

Exemple : `/media/virtual-libraries/ServeurB_Films/`

### Proxy stream

Le plugin expose un endpoint proxy sur le serveur hôte :
```
http://{serverA}:8096/virtuallib/proxy/{connectorId}/{itemId}
```

Ce proxy est utilisé automatiquement dans les `.strm` générés. Il est transparent pour les clients Emby.

**Avantages du proxy** :
- Le token API du serveur source n'est jamais exposé aux clients
- Le serveur A peut transcoder à la volée
- Les clients n'ont pas besoin d'accès réseau direct au serveur source

### Proxy Base URL (paramètre global)

Si le serveur hôte est derrière un reverse proxy, l'URL auto-détectée dans les `.strm` peut être incorrecte. Configurer le champ **Proxy Base URL** :

```
Proxy Base URL : https://media.example.com/emby2
```

Cette URL sera utilisée comme base pour toutes les URLs générées dans les `.strm`.
Laisser vide pour utiliser la détection automatique via les headers `X-Forwarded-Proto/Host/Port`.

### Sync manuelle via API

Il est possible de déclencher une sync via l'API REST d'Emby A :

```http
POST http://{serverA}:8096/virtuallib/sync
X-Emby-Token: {local_api_key}
Content-Type: application/json

{
  "connectorId": "guid-du-connector"   // optionnel, sync tout si absent
}
```

---

## Dépannage

### Les médias n'apparaissent pas après la sync

1. Vérifier que la sync s'est bien terminée : Tableau de bord → Tâches planifiées → VirtualLib Sync
2. Vérifier que les fichiers `.strm` ont été créés dans `/media/virtual-libraries/`
3. Vérifier que la bibliothèque Emby pointe vers le bon dossier
4. Déclencher manuellement un scan de la bibliothèque : Dashboard → Bibliothèques → Analyser

### Erreur "Connection refused" lors du test

- Vérifier que l'URL du serveur source est correcte (incluant le port et le chemin de base)
- Vérifier que le serveur source est accessible depuis le réseau du serveur hôte
- Vérifier que le pare-feu autorise la connexion

### URLs avec double préfixe (`/emby/emby/Items/...`)

Cause : `ServerUrl` contient déjà le chemin `/emby` et les chemins relatifs du connector l'ajoutent une seconde fois.
Fix : s'assurer que `ServerUrl` inclut le chemin de base (`https://host/emby`) et que les appels API internes n'utilisent que des chemins relatifs sans répéter ce préfixe.

### Lecture qui ne démarre pas

- Vérifier les logs Emby : `{EmbyData}/logs/emby*.log`
- Rechercher des erreurs `VirtualLib.ProxyController`
- Vérifier que le timeout proxy n'est pas trop court pour votre réseau
- Vérifier que l'endpoint proxy répond (ne pas oublier `[Unauthenticated]` sur le DTO)

### Taille du fichier incorrecte / "File ended prematurely"

Symptôme : la taille affichée dans Emby est quelques Ko au lieu de la vraie taille, et ffmpeg échoue avec "File ended prematurely".

Cause : le header `Content-Range` n'est pas forwardé par le ProxyController. En .NET `HttpClient`, `Content-Range` est dans `response.Content.Headers`, pas dans `response.Headers`. Sans ce header, ffprobe croit que la taille du fichier est égale au chunk retourné (ex : 1663 bytes).

Fix : utiliser `remoteResponse.Content.Headers.ContentRange`. Après correction du plugin, faire **Actualiser les métadonnées** sur les items affectés dans Emby.

### Hairpin NAT — pod ne peut pas joindre son propre ingress

Symptôme : le pod Emby appelle `https://media.example.com/...` qui résout vers l'IP externe du routeur, et la requête arrive sur le mauvais serveur (production au lieu du pod de test).

Fix Kubernetes : ajouter des `hostAliases` sur le déploiement pour forcer la résolution DNS interne :

```yaml
spec:
  template:
    spec:
      hostAliases:
      - ip: "10.43.128.134"          # ClusterIP du service traefik
        hostnames:
        - "media.example.com"
```

Obtenir la ClusterIP traefik : `kubectl get svc -n kube-system traefik -o jsonpath='{.spec.clusterIP}'`

Il faut aussi un ingress HTTP (pas seulement HTTPS) pointant vers le pod cible :

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: emby2-http
  namespace: media
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: web
spec:
  ingressClassName: traefik
  rules:
  - host: media.example.com
    http:
      paths:
      - path: /emby2
        pathType: Prefix
        backend:
          service:
            name: emby2
            port:
              number: 18096
```

### Les logs Emby affichent des URLs `http://` malgré HTTPS configuré

Normal : la terminaison TLS est faite par le reverse proxy (traefik). Le serveur Emby derrière le proxy reçoit des connexions HTTP et loggue donc des URLs `http://`. Ce n'est pas un bug.

### La sync ne détecte pas les nouveaux ajouts

- Les items ajoutés sur le serveur distant sont détectés automatiquement à la prochaine sync (aucun `.nfo` local → créé)
- Vérifier l'intervalle de sync configuré (défaut : 6h)
- Déclencher une sync manuelle depuis le dashboard plugin

### Les métadonnées sont incomplètes ou manquantes

- Vérifier que le mode **Remote Sync** ou **Remote Sync Full** est sélectionné (pas Local Scraping)
- Si les métadonnées ne se mettent pas à jour malgré une sync : passer en mode **Remote Sync Full** pour forcer la réécriture des `.nfo`
- Les items dont la récupération de métadonnées a échoué sont comptés "failed" et retentés à la prochaine sync

---

## Structure des fichiers générés

```
/media/virtual-libraries/
├── .index/
│   └── {connectorId}.json          # Index de sync (ne pas modifier manuellement)
│
└── ServeurB_Films/
    ├── Inception (2010)/
    │   ├── Inception (2010).strm   # URL proxy vers le stream
    │   ├── Inception (2010).nfo    # Métadonnées (pas de re-scraping)
    │   ├── poster.jpg              # Artwork téléchargé depuis la source
    │   └── fanart.jpg
    │
    └── Dune (2021)/
        ├── Dune (2021).strm
        ├── Dune (2021).nfo
        └── poster.jpg
```

---

## Synchronisation des états utilisateur (v1.6.0)

À chaque sync, VirtualLib propage depuis le serveur source les états suivants vers la bibliothèque virtuelle locale :

| État | Emby | Plex |
|---|---|---|
| Lu / Non lu | ✅ champ `UserData.Played` | ✅ `viewCount > 0` |
| Nombre de lectures | ✅ `UserData.PlayCount` | ✅ `viewCount` |
| Date dernière lecture | ✅ `UserData.LastPlayedDate` | ✅ `lastViewedAt` (unix timestamp) |
| Favori | ✅ `UserData.IsFavorite` | — (Plex n'expose pas les favoris via API XML) |
| Position de reprise | ✅ `UserData.PlaybackPositionTicks` | ✅ `viewOffset` (ms → ticks) |

**Règle de merge** : les états ne sont jamais réduits. Si vous avez marqué un item "lu" localement, il reste lu même si le serveur source l'indique comme non lu.

**Important — les lectures depuis VirtualLib ne sont pas rapportées au serveur source.** Si vous regardez un film depuis la bibliothèque virtuelle, le serveur Emby/Plex source n'en sera pas informé et ses propres statistiques resteront inchangées. La synchronisation est donc unidirectionnelle (source → hôte). La backpropagation temps réel est prévue dans une future version (issue #34).

---

## Désinstallation

1. Supprimer les bibliothèques virtuelles dans Emby (Dashboard → Bibliothèques)
2. Supprimer le répertoire des bibliothèques virtuelles : `rm -rf /media/virtual-libraries/`
3. Désinstaller le plugin : Dashboard → Plugins → VirtualLib → Désinstaller
4. Redémarrer Emby
