using VirtualLib.Core.Models;

namespace VirtualLib.Core.Filtering;

public enum FilterStatus
{
    Kept,
    Rejected,
    /// <summary>Conservé faute d'information technique exploitable (fail-open, D7).</summary>
    KeptUnknownInfo
}

/// <summary>Résultat de l'évaluation d'un item par <see cref="MediaFilterEngine"/>.</summary>
public sealed class FilterResult
{
    public FilterStatus Status { get; init; }
    public string? Reason { get; init; }

    public static readonly FilterResult Kept = new() { Status = FilterStatus.Kept };

    public static FilterResult Rejected(string reason) => new() { Status = FilterStatus.Rejected, Reason = reason };

    public static FilterResult KeptUnknownInfo(string reason) => new() { Status = FilterStatus.KeptUnknownInfo, Reason = reason };
}

/// <summary>
/// Moteur de filtrage additif des médias distants (#44) : résolution minimale, langues audio,
/// langues de sous-titres. Classe pure et sans dépendance (ni logger, ni I/O) — entièrement
/// testable en unitaire.
///
/// Sémantique : ET entre critères, OU à l'intérieur d'un critère. Portée : Movie/Episode
/// uniquement. Fail-open : une information indéterminable ne rejette jamais un item, elle
/// est seulement signalée (<see cref="FilterStatus.KeptUnknownInfo"/>) pour agrégation/log
/// par l'appelant.
/// </summary>
public static class MediaFilterEngine
{
    /// <summary>
    /// Évalue un item contre les règles de filtrage.
    /// <paramref name="streams"/> doit être la liste effective de pistes pour cet item :
    /// <c>item.Technical?.Streams</c> si non vide, sinon le résultat de
    /// <c>IMediaServerConnector.GetStreamInfoAsync</c> pour son RemoteId (D5).
    /// </summary>
    public static FilterResult Evaluate(MediaItem item, IReadOnlyList<MediaStreamInfo> streams, MediaFilterConfig config)
    {
        if (!config.IsActive)
            return FilterResult.Kept;

        // Portée D8 : seuls Movie et Episode sont évalués.
        if (item.Type is not (MediaType.Movie or MediaType.Episode))
            return FilterResult.Kept;

        streams ??= Array.Empty<MediaStreamInfo>();

        // Le filtre langue/sous-titres ne peut s'exprimer que si des pistes ont pu être
        // récupérées (liste vide = information indisponible, D7) — le filtre résolution a son
        // propre repli sur TechnicalInfo.Height et n'en dépend pas.
        var streamsAvailable = streams.Count > 0;

        var rejectReasons  = new List<string>();
        var unknownReasons = new List<string>();

        EvaluateResolution(item, streams, streamsAvailable, config, rejectReasons, unknownReasons);
        EvaluateLanguage(streams, streamsAvailable, config.AudioLanguages, MediaStreamKind.Audio,
            ignoreForced: false, "langue audio", rejectReasons, unknownReasons);
        EvaluateLanguage(streams, streamsAvailable, config.SubtitleLanguages, MediaStreamKind.Subtitle,
            ignoreForced: config.IgnoreForcedSubtitles, "langue de sous-titres", rejectReasons, unknownReasons);

        if (rejectReasons.Count > 0)
            return FilterResult.Rejected(string.Join("; ", rejectReasons));

        if (unknownReasons.Count > 0)
            return FilterResult.KeptUnknownInfo(string.Join("; ", unknownReasons));

        return FilterResult.Kept;
    }

    private static void EvaluateResolution(
        MediaItem item,
        IReadOnlyList<MediaStreamInfo> streams,
        bool streamsAvailable,
        MediaFilterConfig config,
        List<string> rejectReasons,
        List<string> unknownReasons)
    {
        if (config.MinHeight <= 0) return;

        int? videoHeight = streamsAvailable
            ? streams.Where(s => s.Kind == MediaStreamKind.Video)
                     .Select(s => s.Height)
                     .FirstOrDefault(h => h.HasValue)
            : null;

        // Repli sur le scalaire historique si aucune piste vidéo listée (plan tâche 10).
        videoHeight ??= item.Technical?.Height;

        if (videoHeight is null)
            unknownReasons.Add("résolution indéterminable");
        else if (videoHeight.Value < config.MinHeight)
            rejectReasons.Add($"résolution {videoHeight}p < {config.MinHeight}p requis");
    }

    private static void EvaluateLanguage(
        IReadOnlyList<MediaStreamInfo> streams,
        bool streamsAvailable,
        List<string> requiredLanguages,
        MediaStreamKind kind,
        bool ignoreForced,
        string label,
        List<string> rejectReasons,
        List<string> unknownReasons)
    {
        if (requiredLanguages.Count == 0) return;

        if (!streamsAvailable)
        {
            unknownReasons.Add($"{label} indéterminable");
            return;
        }

        var required = NormalizeSet(requiredLanguages);
        var candidates = streams.Where(s => s.Kind == kind);
        if (ignoreForced)
            candidates = candidates.Where(s => !s.IsForced);

        var matches = candidates.Any(s => s.LanguageCode is not null && required.Contains(s.LanguageCode));
        if (!matches)
            rejectReasons.Add($"aucune piste {label} correspondante");
    }

    private static HashSet<string> NormalizeSet(IEnumerable<string> codes) =>
        codes.Select(LanguageMatcher.Normalize)
             .Where(c => c is not null)
             .Select(c => c!)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
