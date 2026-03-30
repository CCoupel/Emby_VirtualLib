using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Core;

/// <summary>
/// Ensures Emby virtual folders exist for each synced connector library.
/// Structure: virtualLibRoot/{ConnectorName}/{LibraryName}
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
    /// Creates (if absent) an Emby virtual folder for the given connector + library.
    /// The folder name is "{connectorName} — {libraryName}".
    /// </summary>
    public void EnsureVirtualFolder(
        string connectorName,
        string libraryName,
        string libraryType,
        string virtualLibRoot)
    {
        var folderPath = Path.Combine(
            virtualLibRoot,
            StrmGenerator.SanitizeName(connectorName),
            StrmGenerator.SanitizeName(libraryName));

        Directory.CreateDirectory(folderPath);

        // Check whether a virtual folder already contains this path
        var existing = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => f.Locations != null &&
                f.Locations.Any(l => string.Equals(l, folderPath, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            _logger.LogDebug(
                "Virtual folder for '{FolderPath}' already exists as '{Name}'",
                folderPath, existing.Name);
            return;
        }

        var virtualFolderName = $"{connectorName} \u2014 {libraryName}";
        var collectionType = MapCollectionType(libraryType);

        _logger.LogInformation(
            "Creating virtual folder '{Name}' (type={Type}) at '{Path}'",
            virtualFolderName, collectionType, folderPath);

        try
        {
            _libraryManager.AddVirtualFolder(virtualFolderName, collectionType, new LibraryOptions(), refreshLibrary: false);

            // Retrieve the CollectionFolder entity just created, then add the path
            var folder = _libraryManager.RootFolder
                .GetChildren(null, CancellationToken.None)
                .OfType<CollectionFolder>()
                .FirstOrDefault(f => string.Equals(f.Name, virtualFolderName, StringComparison.OrdinalIgnoreCase));

            if (folder != null)
                _libraryManager.AddMediaPaths(folder, new[] { new MediaPathInfo { Path = folderPath } }, refreshLibrary: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create virtual folder '{Name}'", virtualFolderName);
        }
    }

    private static string MapCollectionType(string libraryType) => libraryType switch
    {
        "Movies" => "movies",
        "TvShows" => "tvshows",
        "Music" => "music",
        _ => ""
    };
}
