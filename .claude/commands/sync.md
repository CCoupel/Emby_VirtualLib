# Commande /sync — Synchronisation manuelle

## Usage
```
/sync [connectorId]
```

## Description
Déclenche manuellement une synchronisation complète ou ciblée des bibliothèques virtuelles.

## Comportement

1. Si `connectorId` fourni : sync uniquement ce connecteur
2. Si absent : sync tous les connecteurs actifs

## Étapes de la sync

Pour chaque connecteur actif :
- Appeler `TestConnectionAsync()` — arrêter si KO
- Appeler `ListLibrariesAsync()` — récupérer les bibliothèques configurées
- Pour chaque bibliothèque :
  - `ListItemsAsync(libraryId)` — liste complète des items
  - Calculer le delta avec l'index local `.index/{connectorId}.json`
  - Générer `.strm` + `.nfo` + artwork pour les nouveaux items
  - Supprimer les fichiers locaux pour les items supprimés
  - Mettre à jour l'index local
- Déclencher un scan de la bibliothèque virtuelle via `ILibraryManager`

## Logs attendus

```
[INFO]  VirtualLib.SyncJob: Starting sync for connector 'ServeurB' (Emby)
[INFO]  VirtualLib.SyncJob: Found 3 libraries: Films, Séries, Musique
[INFO]  VirtualLib.SyncJob: Films: 342 remote items, 340 local, +2 new, 0 deleted
[INFO]  VirtualLib.SyncJob: Generated: /virtual/Films/Dune (2021)/Dune (2021).strm
[INFO]  VirtualLib.SyncJob: Sync completed in 4.2s
```

## Fichiers à modifier / créer

- `src/VirtualLib/Core/LibrarySyncJob.cs` — logique principale
- `src/VirtualLib/Core/SyncIndex.cs` — lecture/écriture de l'index JSON
- Tests : `tests/VirtualLib.Tests/Core/LibrarySyncJobTests.cs`
