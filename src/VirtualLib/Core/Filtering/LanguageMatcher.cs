namespace VirtualLib.Core.Filtering;

/// <summary>
/// Normalise les codes/libellés de langue hétérogènes renvoyés par les serveurs distants
/// (ISO 639-1, 639-2/B, 639-2/T, ou libellé anglais/français) vers un code canonique
/// 3 lettres minuscules (#44). Un code non reconnu est conservé tel quel en minuscules,
/// sans exception levée.
/// </summary>
public static class LanguageMatcher
{
    private static readonly IReadOnlyDictionary<string, string> AliasToCanonical = BuildMap();

    /// <summary>
    /// Normalise un code ou libellé de langue vers son code canonique 3 lettres.
    /// Retourne null si <paramref name="language"/> est null ou vide.
    /// </summary>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;

        var trimmed = language.Trim();
        return AliasToCanonical.TryGetValue(trimmed, out var canonical)
            ? canonical
            : trimmed.ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string canonical, params string[] aliases)
        {
            map[canonical] = canonical;
            foreach (var alias in aliases)
                map[alias] = canonical;
        }

        Add("fra", "fr", "fre", "french", "français", "francais");
        Add("eng", "en", "english");
        Add("spa", "es", "spanish");
        Add("deu", "de", "ger", "german");
        Add("ita", "it", "italian");
        Add("por", "pt", "portuguese");
        Add("nld", "nl", "dut", "dutch");
        Add("jpn", "ja", "japanese");
        Add("kor", "ko", "korean");
        Add("zho", "zh", "chi", "chinese");
        Add("rus", "ru", "russian");
        Add("ara", "ar", "arabic");

        return map;
    }
}
