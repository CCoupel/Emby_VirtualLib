namespace VirtualLib.Core.Models;

/// <summary>
/// Règles de filtrage additif des médias distants, portées par ConnectorConfig.MediaFilter (#44).
/// Sémantique : ET entre critères de nature différente, OU à l'intérieur d'un critère.
/// Portée : uniquement les types Movie et Episode — tout autre MediaType est conservé sans évaluation.
/// Fail-open : un item dont l'information technique est indisponible est conservé.
/// </summary>
public sealed class MediaFilterConfig
{
    /// <summary>Hauteur vidéo minimale en pixels (720, 1080, 2160…). 0 = règle inactive.</summary>
    public int MinHeight { get; init; }

    /// <summary>Codes langue audio acceptés — OU entre eux. Vide = règle inactive.</summary>
    public List<string> AudioLanguages { get; init; } = new();

    /// <summary>Codes langue sous-titres acceptés — OU entre eux. Vide = règle inactive.</summary>
    public List<string> SubtitleLanguages { get; init; } = new();

    /// <summary>Exclut les pistes sous-titres IsForced du test SubtitleLanguages.</summary>
    public bool IgnoreForcedSubtitles { get; init; }

    /// <summary>
    /// Vrai si au moins un critère est non neutre. Si faux, le moteur n'est pas invoqué.
    /// Défensif contre les listes null (`?.Count ?? 0`) : un ConnectorConfig désérialisé depuis un
    /// XML de configuration antérieur à #44 (donc sans nœud MediaFilter/AudioLanguages/
    /// SubtitleLanguages) ne doit jamais lever de NullReferenceException ici — le comportement
    /// d'IXmlSerializer d'Emby sur un type complexe absent du XML n'est pas garanti respecter les
    /// initialiseurs de propriété C# (urgence #44 — popup d'erreur au chargement de la page sur
    /// des connecteurs préexistants).
    /// </summary>
    public bool IsActive =>
        MinHeight > 0
        || (AudioLanguages?.Count ?? 0) > 0
        || (SubtitleLanguages?.Count ?? 0) > 0;
}
