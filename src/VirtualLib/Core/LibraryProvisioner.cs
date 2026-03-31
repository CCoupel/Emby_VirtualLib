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
    /// Creates an Emby virtual folder for the given connector + library if it does not already exist.
    /// </summary>
    public void EnsureVirtualFolder(
        string connectorName,
        string libraryName,
        string libraryType,
        string virtualLibRoot)
    {
        var virtualFolderName = BuildFolderName(connectorName, libraryName);

        if (FolderExists(virtualFolderName))
        {
            _logger.LogDebug("Virtual folder '{Name}' already exists — skipping", virtualFolderName);
            return;
        }

        var folderPath = BuildFolderPath(virtualLibRoot, connectorName, libraryName);
        Directory.CreateDirectory(folderPath);

        var collectionType = MapCollectionType(libraryType);

        _logger.LogInformation(
            "Creating virtual folder '{Name}' (type={Type}) → '{Path}'",
            virtualFolderName, collectionType, folderPath);

        try
        {
            // Step 1: create without PathInfos so Emby uses collectionType correctly.
            // ContentType must also be set in LibraryOptions — the standalone parameter is not enough.
            _libraryManager.AddVirtualFolder(virtualFolderName, collectionType,
                new LibraryOptions { ContentType = collectionType }, refreshLibrary: false);

            // Step 2: find the newly created folder and attach the physical path
            var created = _libraryManager.GetVirtualFolders()
                .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

            if (created != null && long.TryParse(created.ItemId, out var itemId) &&
                _libraryManager.GetItemById(itemId) is CollectionFolder collectionFolder)
            {
                _libraryManager.AddMediaPaths(
                    collectionFolder,
                    new[] { new MediaPathInfo { Path = folderPath } },
                    refreshLibrary: true);
            }
            else
            {
                _logger.LogWarning("Could not resolve CollectionFolder for '{Name}' to add media path", virtualFolderName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create virtual folder '{Name}'", virtualFolderName);
        }
    }

    /// <summary>
    /// Removes the Emby virtual folder for the given connector + library if it exists.
    /// Does NOT delete the .strm/.nfo files on disk.
    /// </summary>
    public void RemoveVirtualFolder(string connectorName, string libraryName)
    {
        var virtualFolderName = BuildFolderName(connectorName, libraryName);

        var folder = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

        if (folder == null)
        {
            _logger.LogDebug("Virtual folder '{Name}' not found — nothing to remove", virtualFolderName);
            return;
        }

        _logger.LogInformation("Removing virtual folder '{Name}' (ItemId={Id})", virtualFolderName, folder.ItemId);

        if (string.IsNullOrEmpty(folder.ItemId) || !long.TryParse(folder.ItemId, out var internalId))
        {
            _logger.LogWarning("Virtual folder '{Name}' has unexpected ItemId '{Id}'", virtualFolderName, folder.ItemId);
            return;
        }

        try
        {
            _libraryManager.RemoveVirtualFolder(internalId, refreshLibrary: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove virtual folder '{Name}'", virtualFolderName);
        }
    }

    // ------------------------------------------------------------------

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
        "Movies" => "movies",
        "TvShows" => "tvshows",
        "Music" => "music",
        _ => ""
    };
}
