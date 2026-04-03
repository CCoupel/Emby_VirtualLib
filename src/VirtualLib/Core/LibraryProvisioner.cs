using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Core;

/// <summary>
/// Creates/removes Emby virtual folders via the internal library manager.
/// Step 1: AddVirtualFolder without PathInfos so collectionType is preserved.
/// Step 2: AddMediaPaths to associate the physical directory.
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
    /// Creates an Emby virtual folder for the given connector + library if it does not already exist,
    /// then applies LibraryOptions based on the requested metadata mode.
    /// </summary>
    public void EnsureVirtualFolder(
        string connectorName,
        string libraryName,
        string libraryType,
        string virtualLibRoot,
        MetadataMode metadataMode = MetadataMode.RemoteSync)
    {
        var virtualFolderName = BuildFolderName(connectorName, libraryName);
        var folderPath = BuildFolderPath(virtualLibRoot, connectorName, libraryName);
        var collectionType = MapCollectionType(libraryType);

        Directory.CreateDirectory(folderPath);

        var existing = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // Folder already registered in Emby — ensure the physical path is associated.
            // (AddVirtualFolder may have succeeded previously but AddMediaPaths may have failed,
            // leaving a folder with an empty path that Emby cannot scan.)
            ApplyLibraryOptions(virtualFolderName, collectionType, metadataMode);
            EnsureMediaPath(existing, virtualFolderName, folderPath);
            return;
        }

        _logger.LogInformation(
            "Creating virtual folder '{Name}' (type={Type}, mode={Mode}) → '{Path}'",
            virtualFolderName, collectionType, metadataMode, folderPath);

        try
        {
            _libraryManager.AddVirtualFolder(virtualFolderName, collectionType,
                BuildLibraryOptions(collectionType, metadataMode), refreshLibrary: false);

            var created = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

            EnsureMediaPath(created, virtualFolderName, folderPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create virtual folder '{Name}'", virtualFolderName);
        }
    }

    private void EnsureMediaPath(MediaBrowser.Model.Entities.VirtualFolderInfo? folder, string virtualFolderName, string folderPath)
    {
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
    /// Removes the Emby virtual folder for the given connector + library if it exists,
    /// then deletes the corresponding .strm/.nfo files from disk.
    /// </summary>
    public void RemoveVirtualFolder(string connectorName, string libraryName, string virtualLibRoot)
    {
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

        // Delete physical files from disk regardless of whether the Emby folder existed
        if (!string.IsNullOrEmpty(virtualLibRoot))
        {
            var folderPath = BuildFolderPath(virtualLibRoot, connectorName, libraryName);
            if (Directory.Exists(folderPath))
            {
                try
                {
                    Directory.Delete(folderPath, recursive: true);
                    _logger.LogInformation("Deleted virtual library directory '{Path}'", folderPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete virtual library directory '{Path}'", folderPath);
                }
            }
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

            collectionFolder.UpdateLibraryOptions(BuildLibraryOptions(collectionType, mode));
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
            // Emby's own fetchers + NFO writer handle everything
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

    private bool FolderExists(string name) =>
        _libraryManager.GetVirtualFolders()
            .Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string BuildFolderName(string connectorName, string libraryName) =>
        $"{connectorName} \u2014 {libraryName}";

    public static string BuildFolderPath(string virtualLibRoot, string connectorName, string libraryName) =>
        Path.Combine(virtualLibRoot,
            StrmGenerator.SanitizeName(connectorName),
            StrmGenerator.SanitizeName(libraryName));

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
