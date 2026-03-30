using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Core;

/// <summary>
/// Ensures Emby virtual folders exist (or are removed) for connector libraries.
/// Structure: virtualLibRoot/{ConnectorName}/{LibraryName}
/// Virtual folder name: "{ConnectorName} — {LibraryName}"
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
        var options = new LibraryOptions
        {
            PathInfos = new[] { new MediaPathInfo { Path = folderPath } }
        };

        _logger.LogInformation(
            "Creating virtual folder '{Name}' (type={Type}) → '{Path}'",
            virtualFolderName, collectionType, folderPath);

        try
        {
            _libraryManager.AddVirtualFolder(virtualFolderName, collectionType, options, refreshLibrary: true);
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
        try
        {
            var itemGuid = Guid.Parse(folder.ItemId);
            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null)
            {
                _logger.LogWarning("Could not resolve entity for virtual folder '{Name}'", virtualFolderName);
                return;
            }
            _libraryManager.RemoveVirtualFolder(item.InternalId, refreshLibrary: true);
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
