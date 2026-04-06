using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace VirtualLib.Core;

/// <summary>
/// Creates/removes Emby virtual folders via the internal library manager.
///
/// Physical layout (identical for both modes):
///   virtualLibRoot / ConnectorName / LibraryName / items...
///
/// Isolated  : one dedicated Emby library per connector-library pair.
/// SharedByType: one shared Emby library per content type; each connector-library
///              adds its own path to that shared library.
/// </summary>
public sealed class LibraryProvisioner
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryProvisioner> _logger;

    public LibraryProvisioner(ILibraryManager libraryManager, ILogger<LibraryProvisioner> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Creates/updates the Emby virtual folder for the given connector + library.
    /// In SharedByType mode the shared library is created once and each connector-library
    /// path is added individually.
    /// </summary>
    public void EnsureVirtualFolder(
        string connectorName,
        string libraryName,
        string libraryType,
        string virtualLibRoot,
        MetadataMode metadataMode = MetadataMode.RemoteSync,
        LibraryOrganization organization = LibraryOrganization.Isolated,
        string sharedLibraryPrefix = "",
        string sharedLibrarySuffix = "")
    {
        var normalizedType = NormalizeLibraryType(libraryType);
        var virtualFolderName = organization == LibraryOrganization.SharedByType
            ? BuildSharedFolderName(normalizedType, sharedLibraryPrefix, sharedLibrarySuffix)
            : BuildFolderName(connectorName, libraryName);

        // Physical path is always virtualLibRoot/ConnectorName/LibraryName/ (same for both modes).
        var folderPath = BuildFolderPath(virtualLibRoot, connectorName, libraryName);
        var collectionType = MapCollectionType(normalizedType);

        Directory.CreateDirectory(folderPath);

        var existing = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            ApplyLibraryOptions(virtualFolderName, collectionType, metadataMode);
        }
        else
        {
            _logger.LogInformation(
                "Creating virtual folder '{Name}' (type={Type}, mode={Mode})",
                virtualFolderName, collectionType, metadataMode);

            try
            {
                _libraryManager.AddVirtualFolder(virtualFolderName, collectionType,
                    BuildLibraryOptions(collectionType, metadataMode), refreshLibrary: false);

                // GetVirtualFolders() may not reflect the new folder immediately; retry a few times.
                for (var attempt = 0; attempt < 5 && existing == null; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(300);
                    existing = _libraryManager.GetVirtualFolders()
                        .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create virtual folder '{Name}'", virtualFolderName);
                return;
            }
        }

        // Add this connector-library path to the (possibly newly created) library.
        EnsureMediaPath(virtualFolderName, folderPath);
    }

    /// <summary>
    /// Re-fetches the virtual folder info fresh (avoids stale Locations cache) then adds
    /// the media path if not already present.
    /// </summary>
    private void EnsureMediaPath(string virtualFolderName, string folderPath)
    {
        // Always fetch fresh — the cached VirtualFolderInfo.Locations may lag after AddMediaPaths.
        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

        if (folder == null)
        {
            _logger.LogWarning("Could not resolve virtual folder '{Name}' to add media path", virtualFolderName);
            return;
        }

        // Already has this path registered — nothing to do.
        if (folder.Locations != null && folder.Locations.Any(l =>
                string.Equals(l, folderPath, StringComparison.OrdinalIgnoreCase)))
            return;

        if (!long.TryParse(folder.ItemId, out var itemId) ||
            _libraryManager.GetItemById(itemId) is not CollectionFolder collectionFolder)
        {
            _logger.LogWarning(
                "Could not resolve CollectionFolder for '{Name}' (ItemId={Id}) to add media path",
                virtualFolderName, folder.ItemId);
            return;
        }

        _logger.LogInformation("Adding media path '{Path}' to virtual folder '{Name}'", folderPath, virtualFolderName);
        _libraryManager.AddMediaPaths(
            collectionFolder,
            new[] { new MediaPathInfo { Path = folderPath } },
            refreshLibrary: true);
    }

    /// <summary>
    /// Removes the virtual folder (or one path from a shared folder) and deletes files from disk.
    ///
    /// Isolated  : removes the dedicated Emby library + physical folder.
    /// SharedByType:
    ///   – always deletes the physical connector-library subfolder.
    ///   – if <paramref name="removeSharedEmbyLibrary"/> is true: removes the entire shared Emby library.
    ///   – otherwise: removes only this connector's path from the shared library.
    /// </summary>
    public void RemoveVirtualFolder(
        string connectorName,
        string libraryName,
        string virtualLibRoot,
        string libraryType = "",
        LibraryOrganization organization = LibraryOrganization.Isolated,
        bool removeSharedEmbyLibrary = false,
        string sharedLibraryPrefix = "",
        string sharedLibrarySuffix = "")
    {
        // Physical path is always virtualLibRoot/ConnectorName/LibraryName/ (same for both modes).
        var folderPath = string.IsNullOrEmpty(virtualLibRoot)
            ? string.Empty
            : BuildFolderPath(virtualLibRoot, connectorName, libraryName);

        if (organization == LibraryOrganization.SharedByType)
        {
            if (!string.IsNullOrEmpty(folderPath))
                DeleteDirectory(folderPath);

            var normalizedType = NormalizeLibraryType(libraryType);
            var sharedFolderName = BuildSharedFolderName(normalizedType, sharedLibraryPrefix, sharedLibrarySuffix);

            var sharedFolder = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => string.Equals(f.Name, sharedFolderName, StringComparison.OrdinalIgnoreCase));

            if (sharedFolder == null)
                return;

            if (!long.TryParse(sharedFolder.ItemId, out var itemId))
            {
                _logger.LogWarning("Shared folder '{Name}' has unexpected ItemId '{Id}'",
                    sharedFolderName, sharedFolder.ItemId);
                return;
            }

            if (removeSharedEmbyLibrary)
            {
                try
                {
                    _logger.LogInformation("Removing shared virtual folder '{Name}' (no connectors remain for type={Type})",
                        sharedFolderName, normalizedType);
                    _libraryManager.RemoveVirtualFolder(itemId, refreshLibrary: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove shared virtual folder '{Name}'", sharedFolderName);
                }
            }
            else if (!string.IsNullOrEmpty(folderPath))
            {
                // Only remove this connector's path — the shared library stays for other connectors.
                var hasPath = sharedFolder.Locations?.Any(l =>
                    string.Equals(l, folderPath, StringComparison.OrdinalIgnoreCase)) == true;

                if (!hasPath)
                    return;

                try
                {
                    _logger.LogInformation("Removing path '{Path}' from shared folder '{Name}'",
                        folderPath, sharedFolderName);
                    _libraryManager.RemoveMediaPath(itemId, folderPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove media path from shared folder '{Name}'", sharedFolderName);
                }
            }

            return;
        }

        // Isolated mode: remove the dedicated Emby library + physical folder.
        var virtualFolderName = BuildFolderName(connectorName, libraryName);

        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

        if (folder != null)
        {
            _logger.LogInformation("Removing virtual folder '{Name}' (ItemId={Id})", virtualFolderName, folder.ItemId);

            if (string.IsNullOrEmpty(folder.ItemId) || !long.TryParse(folder.ItemId, out var internalId))
            {
                _logger.LogWarning("Virtual folder '{Name}' has unexpected ItemId '{Id}'", virtualFolderName, folder.ItemId);
            }
            else
            {
                try
                {
                    _libraryManager.RemoveVirtualFolder(internalId, refreshLibrary: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to remove virtual folder '{Name}'", virtualFolderName);
                }
            }
        }
        else
        {
            _logger.LogDebug("Virtual folder '{Name}' not found in Emby — skipping folder removal", virtualFolderName);
        }

        if (!string.IsNullOrEmpty(folderPath))
            DeleteDirectory(folderPath);
    }

    private void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
            _logger.LogInformation("Deleted virtual library directory '{Path}'", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete virtual library directory '{Path}'", path);
        }
    }

    // ------------------------------------------------------------------

    private void ApplyLibraryOptions(string virtualFolderName, string collectionType, MetadataMode mode)
    {
        try
        {
            var folder = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

            if (folder == null || !long.TryParse(folder.ItemId, out var itemId)) return;
            if (_libraryManager.GetItemById(itemId) is not CollectionFolder collectionFolder) return;

            // Preserve existing PathInfos — UpdateLibraryOptions would otherwise clear all media paths.
            var newOpts = BuildLibraryOptions(collectionType, mode);
            newOpts.PathInfos = _libraryManager.GetLibraryOptions(collectionFolder)?.PathInfos;
            collectionFolder.UpdateLibraryOptions(newOpts);
            _logger.LogDebug("Updated LibraryOptions for '{Name}' (mode={Mode})", virtualFolderName, mode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update LibraryOptions for '{Name}'", virtualFolderName);
        }
    }

    private static LibraryOptions BuildLibraryOptions(string collectionType, MetadataMode mode)
    {
        // Common options applied regardless of metadata mode
        var options = new LibraryOptions
        {
            ContentType = collectionType,
            CacheImages = true,
            DownloadImagesInAdvance = true,
            EnableChapterImageExtraction = true,
            ExtractChapterImagesDuringLibraryScan = true,
            AutoGenerateChapters = true,
            AutoGenerateChapterIntervalMinutes = 5,
            SaveLocalThumbnailSets = true,
        };

        if (mode == MetadataMode.LocalScraping)
        {
            options.SaveLocalMetadata = true;
            options.MetadataSavers = new[] { "Nfo" };

            var movieFetchers = new[] { "TheMovieDb", "The Open Movie Database" };
            var tvFetchers    = new[] { "TheMovieDb", "TheTVDB" };
            var imageFetchers = new[] { "TheMovieDb", "FanArt" };

            options.TypeOptions = new[]
            {
                new TypeOptions
                {
                    Type = "Movie",
                    MetadataFetchers = movieFetchers,
                    MetadataFetcherOrder = movieFetchers,
                    ImageFetchers = imageFetchers,
                    ImageFetcherOrder = imageFetchers
                },
                new TypeOptions
                {
                    Type = "Series",
                    MetadataFetchers = tvFetchers,
                    MetadataFetcherOrder = tvFetchers,
                    ImageFetchers = imageFetchers,
                    ImageFetcherOrder = imageFetchers
                },
                new TypeOptions
                {
                    Type = "Episode",
                    MetadataFetchers = tvFetchers,
                    MetadataFetcherOrder = tvFetchers,
                    ImageFetchers = imageFetchers,
                    ImageFetcherOrder = imageFetchers
                },
                new TypeOptions { Type = "Book",      MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() },
                new TypeOptions { Type = "AudioBook", MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() },
                new TypeOptions { Type = "Photo",     MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() }
            };
        }
        else
        {
            // RemoteSync: plugin writes NFO + downloads images; disable Emby's online fetchers
            options.SaveLocalMetadata = false;
            options.MetadataSavers = Array.Empty<string>();
            options.TypeOptions = new[]
            {
                new TypeOptions { Type = "Movie",     MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() },
                new TypeOptions { Type = "Series",    MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() },
                new TypeOptions { Type = "Episode",   MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() },
                new TypeOptions { Type = "Book",      MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() },
                new TypeOptions { Type = "AudioBook", MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() },
                new TypeOptions { Type = "Photo",     MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() }
            };
        }

        return options;
    }

    public static string BuildFolderName(string connectorName, string libraryName) =>
        $"{connectorName} \u2014 {libraryName}";

    public static string BuildFolderPath(string virtualLibRoot, string connectorName, string libraryName) =>
        Path.Combine(virtualLibRoot,
            StrmGenerator.SanitizeName(connectorName),
            StrmGenerator.SanitizeName(libraryName));

    public static string BuildSharedFolderName(string libraryType, string prefix, string suffix)
    {
        var typeName = string.IsNullOrWhiteSpace(libraryType) ? "Unknown" : libraryType;
        return $"{prefix}{typeName}{suffix}";
    }

    /// <summary>
    /// Normalizes raw library type strings (e.g. from Plex: "movie", "show") to the canonical
    /// VirtualLib values (e.g. "Movies", "TvShows") used as Emby collection types.
    /// </summary>
    public static string NormalizeLibraryType(string? libraryType) =>
        libraryType?.ToLowerInvariant() switch
        {
            "movies" or "movie"                     => "Movies",
            "tvshows" or "tv" or "show" or "shows"  => "TvShows",
            "music"                                  => "Music",
            "books" or "book"                        => "Books",
            "audiobooks" or "audiobook"              => "Audiobooks",
            "photos" or "photo"                      => "Photos",
            _ => string.IsNullOrWhiteSpace(libraryType) ? "Unknown" : libraryType
        };

    private static string MapCollectionType(string libraryType) => libraryType switch
    {
        "Movies"     => "movies",
        "TvShows"    => "tvshows",
        "Music"      => "music",
        "Books"      => "books",
        "Audiobooks" => "audiobooks",
        "Photos"     => "photos",
        _            => ""
    };
}
