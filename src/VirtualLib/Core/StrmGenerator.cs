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
        string libraryName,
        string virtualLibRoot,
        string proxyBaseUrl = "")
    {
        var dirPath = GetDirectoryPath(item, connectorName, libraryName, virtualLibRoot);
        var fileName = GetFileName(item);
        var filePath = Path.Combine(dirPath, fileName + ".strm");
        var baseUrl = string.IsNullOrEmpty(proxyBaseUrl) ? "http://localhost:8096" : proxyBaseUrl;
        var streamUrl = $"{baseUrl.TrimEnd('/')}/virtuallib/proxy/{connectorId}/{item.RemoteId}";

        Directory.CreateDirectory(dirPath);
        File.WriteAllText(filePath, streamUrl);

        return filePath;
    }

    public string GetDirectoryPath(MediaItem item, string connectorName, string libraryName, string virtualLibRoot)
    {
        var safeConnector = SanitizeName(connectorName);
        var safeLibrary = SanitizeName(libraryName);

        if (item.Type == MediaType.Episode)
        {
            var seriesName = SanitizeName(item.SeriesName ?? item.Title);
            var season = item.SeasonNumber ?? 0;
            return Path.Combine(virtualLibRoot, safeConnector, safeLibrary, seriesName, $"Season {season:D2}");
        }
        else
        {
            var folderName = item.Year.HasValue
                ? $"{SanitizeName(item.Title)} ({item.Year})"
                : SanitizeName(item.Title);
            return Path.Combine(virtualLibRoot, safeConnector, safeLibrary, folderName);
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
