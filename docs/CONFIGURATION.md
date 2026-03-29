# Guide de configuration — VirtualLib

## Installation du plugin

### Méthode 1 : Via le catalogue de plugins Emby
*(Disponible après publication)*
Dashboard → Plugins → Catalogue → VirtualLib → Installer

### Méthode 2 : Installation manuelle
1. Télécharger `VirtualLib.dll` depuis les releases GitHub
2. Copier dans `{EmbyData}/plugins/VirtualLib/`
3. Redémarrer Emby Server

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

Cliquer **"Ajouter un serveur"** et remplir :

| Champ | Exemple | Description |
|---|---|---|
| Nom d'affichage | `Serveur B - Bureau` | Nom libre pour identifier la source |
| Type de serveur | `Emby` | Emby / Jellyfin / Plex |
| URL du serveur | `http://192.168.1.200:8096` | URL complète avec port |
| Clé API | `abc123def456...` | Clé API du serveur source |

Cliquer **"Tester la connexion"** pour valider avant de sauvegarder.

### Étape 4 : Sélectionner les bibliothèques à synchroniser

Après un test de connexion réussi, la liste des bibliothèques disponibles sur le serveur source s'affiche. Cocher celles à inclure.

### Étape 5 : Configurer la synchronisation automatique

```
Intervalle de synchronisation : 6 heures  (recommandé)
Timeout proxy stream :          30 secondes
```

### Étape 6 : Lancer la première synchronisation

Cliquer **"Synchroniser maintenant"** pour déclencher la première sync manuellement.

### Étape 7 : Ajouter la bibliothèque virtuelle dans Emby

Après la sync :
1. Dashboard → Bibliothèques → Ajouter une bibliothèque
2. Type : Films (ou Séries selon le contenu)
3. Dossier : `/media/virtual-libraries/Films_ServeurB`
4. Valider

Les médias du serveur B apparaissent maintenant dans l'interface d'Emby A.

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

- Vérifier que l'URL du serveur source est correcte (incluant le port)
- Vérifier que le serveur source est accessible depuis le réseau du serveur hôte
- Vérifier que le pare-feu autorise la connexion

### Lecture qui ne démarre pas

- Vérifier les logs Emby : `{EmbyData}/logs/emby*.log`
- Rechercher des erreurs `VirtualLib.ProxyController`
- Vérifier que le timeout proxy n'est pas trop court pour votre réseau

### La sync ne détecte pas les nouveaux ajouts

- La sync delta compare les IDs distants avec l'index local (`.index/{connectorId}.json`)
- Si l'index est corrompu, le supprimer pour forcer une re-sync complète
- Vérifier l'intervalle de sync configuré

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

## Désinstallation

1. Supprimer les bibliothèques virtuelles dans Emby (Dashboard → Bibliothèques)
2. Supprimer le répertoire des bibliothèques virtuelles : `rm -rf /media/virtual-libraries/`
3. Désinstaller le plugin : Dashboard → Plugins → VirtualLib → Désinstaller
4. Redémarrer Emby
