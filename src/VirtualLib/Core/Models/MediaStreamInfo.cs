namespace VirtualLib.Core.Models;

/// <summary>Nature d'une piste média — cf. contracts/models.md.</summary>
public enum MediaStreamKind
{
    Video,
    Audio,
    Subtitle,
    Other
}

/// <summary>
/// Une piste (vidéo, audio ou sous-titre) d'un média distant.
/// Produite par les connecteurs depuis les MediaStreams Emby / &lt;Stream&gt; Plex.
/// </summary>
public sealed class MediaStreamInfo
{
    public MediaStreamKind Kind { get; init; }

    /// <summary>Index de la piste dans le conteneur.</summary>
    public int? Index { get; init; }

    /// <summary>Codec (h264, ac3, srt…).</summary>
    public string? Codec { get; init; }

    /// <summary>Libellé brut tel que reçu du serveur source (fre, French, Français).</summary>
    public string? Language { get; init; }

    /// <summary>Code canonique 3 lettres minuscules, normalisé par LanguageMatcher. Null si indéterminable.</summary>
    public string? LanguageCode { get; init; }

    public bool IsDefault { get; init; }

    /// <summary>Sous-titre forcé.</summary>
    public bool IsForced { get; init; }

    /// <summary>Piste externe (fichier .srt adjacent).</summary>
    public bool IsExternal { get; init; }

    /// <summary>Piste pour malentendants.</summary>
    public bool IsHearingImpaired { get; init; }

    /// <summary>Vidéo uniquement.</summary>
    public int? Width { get; init; }

    /// <summary>Vidéo uniquement.</summary>
    public int? Height { get; init; }

    /// <summary>Audio uniquement.</summary>
    public int? Channels { get; init; }
}
