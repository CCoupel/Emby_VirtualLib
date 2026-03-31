# Commande /build — Compiler et déployer le plugin

## Usage
```
/build [configuration]
```

Exemples :
```
/build           # Release par défaut + déploiement sur emby2
/build Debug     # Debug uniquement, sans déploiement
```

---

## 1. Build

Le SDK .NET est installé dans le profil utilisateur (pas dans Program Files).

```bash
# Build Release → dist/VirtualLib.dll
"C:/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe" build \
  "C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/src/VirtualLib" \
  --configuration Release \
  --output "C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/dist"
```

Vérifications post-build :
- `dist/VirtualLib.dll` présent et date récente (`stat dist/VirtualLib.dll | grep Modify`)
- `0 Erreur(s)` dans la sortie

---

## 2. Déploiement sur emby2 (Kubernetes)

> **Toujours déployer sur `emby2`, jamais sur `emby`** (emby = production).
> Namespace : `media`. Kubeconfig : `private/kubeconfig.yml`.

### Trouver le nom du pod courant
```bash
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media get pods
# → noter le nom du pod emby2-XXXXXXX-XXXXX
```

### Copier le DLL dans le pod
```bash
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media cp dist/VirtualLib.dll <POD_NAME>:/config/plugins/VirtualLib.dll
```

> **Important** : utiliser un chemin relatif pour la source (`dist/VirtualLib.dll`),
> pas un chemin absolu Windows (Git Bash convertit les `/c/...` ce qui casse kubectl).
> `MSYS_NO_PATHCONV=1` est requis pour éviter la conversion du chemin de destination.

### Redémarrer le déploiement
```bash
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media rollout restart deployment/emby2

# Attendre que le nouveau pod soit prêt
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media rollout status deployment/emby2 --timeout=60s
```

### Vérifier le DLL dans le nouveau pod
```bash
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media exec <NOUVEAU_POD> -- ls -la /config/plugins/VirtualLib.dll
```

---

## 3. Séquence complète (build + deploy)

```bash
# 1. Build
"C:/Users/cyril/AppData/Local/Microsoft/dotnet/dotnet.exe" build \
  "C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/src/VirtualLib" \
  --configuration Release \
  --output "C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/dist"

# 2. Récupérer le nom du pod emby2
POD=$(kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media get pods -l app=emby2 -o jsonpath='{.items[0].metadata.name}')

# 3. Copier le DLL (depuis le répertoire du projet, chemin relatif)
cd C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media cp dist/VirtualLib.dll $POD:/config/plugins/VirtualLib.dll

# 4. Restart + attendre
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media rollout restart deployment/emby2
MSYS_NO_PATHCONV=1 kubectl --kubeconfig C:/Users/cyril/Documents/VScode/GITHUB/Emby_VirtualLib/private/kubeconfig.yml \
  -n media rollout status deployment/emby2 --timeout=60s
```

---

## Notes

- Le SDK .NET 6.0.428 est dans `C:/Users/cyril/AppData/Local/Microsoft/dotnet/`
- Le runtime dans `C:/Program Files/dotnet/` est **uniquement le runtime**, pas le SDK
- Le plugin se charge au démarrage d'Emby — un restart est obligatoire après chaque déploiement
- Après déploiement, relancer une sync depuis l'UI pour régénérer les fichiers .strm
