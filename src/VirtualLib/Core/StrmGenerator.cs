using System.Text.RegularExpressions;
using VirtualLib.Core.Models;

namespace VirtualLib.Core;


public sealed class StrmGenerator
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Génère les fichiers .strm pour un item.
    /// Retourne le chemin du fichier créé.
    /// </summary>
    public string Generate(
        MediaItem item,
        string connectorId,
        string connectorName,
        string libraryId,
        string libraryName,
        string virtualLibRoot,
        string proxyBaseUrl = "",
        LibraryOrganization organization = LibraryOrganization.Isolated,
        string libraryType = "")
    {
        var dirPath = GetDirectoryPath(item, connectorName, libraryName, virtualLibRoot, organization, libraryType);
        var fileName = GetFileName(item);
        var filePath = Path.Combine(dirPath, fileName + ".strm");
        var baseUrl = string.IsNullOrEmpty(proxyBaseUrl) ? "http://localhost:8096" : proxyBaseUrl;
        var streamUrl = $"{baseUrl.TrimEnd('/')}/virtuallib/proxy/{connectorId}/{libraryId}/{item.RemoteId}";

        Directory.CreateDirectory(dirPath);
        File.WriteAllText(filePath, streamUrl);

        return filePath;
    }

    public string GetDirectoryPath(
        MediaItem item,
        string connectorName,
        string libraryName,
        string virtualLibRoot,
        LibraryOrganization organization = LibraryOrganization.Isolated,
        string libraryType = "")
    {
        var safeConnector = SanitizeName(connectorName);
        var safeLibrary = SanitizeName(libraryName);

        var typeFolder = string.IsNullOrWhiteSpace(libraryType) ? "Unknown" : libraryType;
        var libraryBase = organization == LibraryOrganization.SharedByType
            ? Path.Combine(virtualLibRoot, typeFolder, safeConnector, safeLibrary)
            : Path.Combine(virtualLibRoot, safeConnector, safeLibrary);

        if (item.Type == MediaType.Episode)
        {
            var seriesName = SanitizeName(item.SeriesName ?? item.Title);
            var season = item.SeasonNumber ?? 0;
            return Path.Combine(libraryBase, seriesName, $"Season {season:D2}");
        }
        else if (item.Type == MediaType.AudioBook && !string.IsNullOrEmpty(item.SeriesName))
        {
            // Chapitre de livre audio : {livre (année)}/
            var bookFolder = item.Year.HasValue
                ? $"{SanitizeName(item.SeriesName)} ({item.Year})"
                : SanitizeName(item.SeriesName);
            return Path.Combine(libraryBase, bookFolder);
        }
        else
        {
            var folderName = item.Year.HasValue
                ? $"{SanitizeName(item.Title)} ({item.Year})"
                : SanitizeName(item.Title);
            return Path.Combine(libraryBase, folderName);
        }
    }

    public string GetFileName(MediaItem item)
    {
        if (item.Type == MediaType.Episode)
        {
            var seriesName = SanitizeName(item.SeriesName ?? item.Title);
            var s = item.SeasonNumber ?? 0;
            var e = item.EpisodeNumber ?? 0;
            return $"{seriesName} - S{s:D2}E{e:D2}";
        }
        else if (item.Type == MediaType.AudioBook && !string.IsNullOrEmpty(item.SeriesName))
        {
            // {index:D2} - {titre du chapitre}
            var idx = item.EpisodeNumber ?? 0;
            return idx > 0
                ? $"{idx:D2} - {SanitizeName(item.Title)}"
                : SanitizeName(item.Title);
        }
        else
        {
            return item.Year.HasValue
                ? $"{SanitizeName(item.Title)} ({item.Year})"
                : SanitizeName(item.Title);
        }
    }

    public static string SanitizeName(string name)
    {
        var result = string.Concat(name.Select(c => InvalidChars.Contains(c) ? '_' : c));
        // Collapse multiple spaces/underscores
        result = Regex.Replace(result, @"_+", "_").Trim('_', ' ');
        return result;
    }
}
