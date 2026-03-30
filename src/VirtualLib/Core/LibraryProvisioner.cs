using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Core;

/// <summary>
/// Creates/removes Emby virtual folders via the local HTTP API to ensure
/// the collection type is properly set (ILibraryManager.AddVirtualFolder
/// ignores collectionType when PathInfos are provided).
/// </summary>
public sealed class LibraryProvisioner
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryProvisioner> _logger;
    private readonly HttpClient _http;

    public LibraryProvisioner(ILibraryManager libraryManager, ILogger<LibraryProvisioner> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _http = new HttpClient();
    }

    /// <summary>
    /// Creates an Emby virtual folder for the given connector + library if it does not already exist.
    /// </summary>
    public void EnsureVirtualFolder(
        string connectorName,
        string libraryName,
        string libraryType,
        string virtualLibRoot,
        string localBaseUrl,
        string authToken)
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

        // POST /Library/VirtualFolders?name=NAME&collectionType=TYPE&refreshLibrary=false
        var qs = $"name={Uri.EscapeDataString(virtualFolderName)}" +
                 (string.IsNullOrEmpty(collectionType) ? "" : $"&collectionType={collectionType}") +
                 "&refreshLibrary=true";
        var url = $"{localBaseUrl.TrimEnd('/')}/Library/VirtualFolders?{qs}";

        var body = JsonSerializer.Serialize(new { Paths = new[] { folderPath } });
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Emby-Token", authToken);

        try
        {
            var resp = _http.Send(req);
            _logger.LogInformation("CreateVirtualFolder response: {Status}", resp.StatusCode);
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
    public void RemoveVirtualFolder(string connectorName, string libraryName,
        string localBaseUrl, string authToken)
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
