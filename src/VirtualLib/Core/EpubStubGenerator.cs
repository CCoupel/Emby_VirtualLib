using System.IO.Compression;
using System.Text;
using VirtualLib.Core.Models;

namespace VirtualLib.Core;

/// <summary>
/// Génère un fichier .epub minimal valide pour les items de type Book/AudioBook.
/// Emby's BookResolver ignore les fichiers .strm dans les bibliothèques books —
/// seuls les vrais fichiers .epub/.mobi sont scannés.
/// Le stub contient les métadonnées OPF + un lien vers l'URL proxy pour le téléchargement.
/// </summary>
public sealed class EpubStubGenerator
{
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
        var filePath = Path.Combine(dirPath, fileName + ".epub");

        if (!File.Exists(filePath))
        {
            Directory.CreateDirectory(dirPath);
            var proxyUrl = BuildProxyUrl(proxyBaseUrl, connectorId, libraryId, item.RemoteId);
            WriteEpubStub(filePath, item, proxyUrl);
        }

        return filePath;
    }

    public string GetFileName(MediaItem item) =>
        StrmGenerator.SanitizeName(item.Year.HasValue
            ? $"{item.Title} ({item.Year})"
            : item.Title);

    public static string GetDirectoryPath(
        MediaItem item,
        string connectorName,
        string libraryName,
        string virtualLibRoot,
        LibraryOrganization organization = LibraryOrganization.Isolated,
        string libraryType = "")
    {
        var safeConnector = StrmGenerator.SanitizeName(connectorName);
        var safeLibrary   = StrmGenerator.SanitizeName(libraryName);
        var typeFolder    = string.IsNullOrWhiteSpace(libraryType) ? "Unknown" : libraryType;

        var libraryBase = organization == LibraryOrganization.SharedByType
            ? Path.Combine(virtualLibRoot, typeFolder, safeConnector, safeLibrary)
            : Path.Combine(virtualLibRoot, safeConnector, safeLibrary);

        var folderName = item.Year.HasValue
            ? $"{StrmGenerator.SanitizeName(item.Title)} ({item.Year})"
            : StrmGenerator.SanitizeName(item.Title);
        return Path.Combine(libraryBase, folderName);
    }

    private static string BuildProxyUrl(string proxyBaseUrl, string connectorId, string libraryId, string itemId)
    {
        var baseUrl = string.IsNullOrEmpty(proxyBaseUrl) ? "http://localhost:8096" : proxyBaseUrl;
        return $"{baseUrl.TrimEnd('/')}/virtuallib/proxy/{connectorId}/{libraryId}/{itemId}";
    }

    private static void WriteEpubStub(string filePath, MediaItem item, string proxyUrl)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // mimetype doit être le premier fichier et non compressé (spec EPUB)
        var mimetypeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var sw = new StreamWriter(mimetypeEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            sw.Write("application/epub+zip");

        // META-INF/container.xml
        var containerEntry = zip.CreateEntry("META-INF/container.xml");
        using (var sw = new StreamWriter(containerEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            sw.Write(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<container version=\"1.0\" xmlns=\"urn:oasis:schemas:container\">\n" +
                "  <rootfiles>\n" +
                "    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n" +
                "  </rootfiles>\n" +
                "</container>");

        // OEBPS/content.opf — contient les métadonnées lues par Emby
        var title = XmlEscape(item.Title);
        var opfEntry = zip.CreateEntry("OEBPS/content.opf");
        using (var sw = new StreamWriter(opfEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">");
            sb.AppendLine("  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
            sb.AppendLine($"    <dc:title>{title}</dc:title>");
            if (item.Year.HasValue)
                sb.AppendLine($"    <dc:date>{item.Year}</dc:date>");
            sb.AppendLine($"    <dc:identifier id=\"bookid\">{XmlEscape(item.RemoteId)}</dc:identifier>");
            sb.AppendLine("    <dc:language>fr</dc:language>");
            sb.AppendLine("  </metadata>");
            sb.AppendLine("  <manifest>");
            sb.AppendLine("    <item id=\"content\" href=\"content.xhtml\" media-type=\"application/xhtml+xml\"/>");
            sb.AppendLine("  </manifest>");
            sb.AppendLine("  <spine><itemref idref=\"content\"/></spine>");
            sb.AppendLine("</package>");
            sw.Write(sb.ToString());
        }

        // OEBPS/content.xhtml — contenu minimal avec lien de téléchargement
        var contentEntry = zip.CreateEntry("OEBPS/content.xhtml");
        using (var sw = new StreamWriter(contentEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            sw.Write(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE html>\n" +
                "<html xmlns=\"http://www.w3.org/1999/xhtml\">\n" +
                $"  <head><title>{title}</title></head>\n" +
                "  <body>\n" +
                $"    <h1>{title}</h1>\n" +
                $"    <p><a href=\"{XmlEscape(proxyUrl)}\">Télécharger depuis la bibliothèque source</a></p>\n" +
                "  </body>\n" +
                "</html>");
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;");
}
