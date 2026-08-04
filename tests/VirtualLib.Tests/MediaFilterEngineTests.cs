using System;
using System.Collections.Generic;
using VirtualLib.Core.Filtering;
using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Tests unitaires de <see cref="MediaFilterEngine"/> (#44) — table de cas couvrant CA1 à CA7
/// du plan (_work/reports/planner-20260804-164559.md). Classe pure et sans dépendance
/// (ni logger, ni I/O) : chaque scénario s'exprime en un appel direct à <c>Evaluate</c>.
/// </summary>
public class MediaFilterEngineTests
{
    private static MediaItem Movie(TechnicalInfo? technical = null) => new()
    {
        RemoteId = "m1",
        Title = "Test Movie",
        Type = MediaType.Movie,
        Year = 2020,
        Technical = technical
    };

    private static MediaItem Episode() => new()
    {
        RemoteId = "e1",
        Title = "Pilot",
        Type = MediaType.Episode,
        SeriesName = "Show",
        SeasonNumber = 1,
        EpisodeNumber = 1
    };

    private static MediaStreamInfo Video(int height) => new()
    {
        Kind = MediaStreamKind.Video,
        Height = height
    };

    private static MediaStreamInfo Audio(string languageCode) => new()
    {
        Kind = MediaStreamKind.Audio,
        Language = languageCode,
        LanguageCode = languageCode
    };

    private static MediaStreamInfo Subtitle(string languageCode, bool isForced = false) => new()
    {
        Kind = MediaStreamKind.Subtitle,
        Language = languageCode,
        LanguageCode = languageCode,
        IsForced = isForced
    };

    // ---- CA1 — config neutre : court-circuit, aucune évaluation ----

    [Fact]
    public void Evaluate_NeutralConfig_KeepsItem_WithoutEvaluation()
    {
        var config = new MediaFilterConfig(); // tous les champs neutres
        Assert.False(config.IsActive);

        // Aucune information technique fournie : si le moteur évaluait malgré tout (au lieu de
        // court-circuiter), un comportement fail-open correct renverrait KeptUnknownInfo — cette
        // assertion vérifie précisément le court-circuit du D2 (Kept, pas KeptUnknownInfo).
        var result = MediaFilterEngine.Evaluate(Movie(), Array.Empty<MediaStreamInfo>(), config);

        Assert.Equal(FilterStatus.Kept, result.Status);
    }

    // ---- CA2 — résolution seule ----

    [Theory]
    [InlineData(720, FilterStatus.Rejected)]
    [InlineData(1080, FilterStatus.Kept)]
    [InlineData(2160, FilterStatus.Kept)]
    public void Evaluate_MinHeight1080_FiltersOnVideoStreamHeight(int actualHeight, FilterStatus expected)
    {
        var config = new MediaFilterConfig { MinHeight = 1080 };
        var streams = new[] { Video(actualHeight) };

        var result = MediaFilterEngine.Evaluate(Movie(), streams, config);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void Evaluate_MinHeight_FallsBackToTechnicalInfoHeight_WhenNoVideoStreamListed()
    {
        var config = new MediaFilterConfig { MinHeight = 1080 };
        var item = Movie(technical: new TechnicalInfo { Height = 1080 });

        var result = MediaFilterEngine.Evaluate(item, Array.Empty<MediaStreamInfo>(), config);

        Assert.Equal(FilterStatus.Kept, result.Status);
    }

    [Fact]
    public void Evaluate_MinHeight_FallbackScalar_BelowThreshold_IsRejected()
    {
        var config = new MediaFilterConfig { MinHeight = 1080 };
        var item = Movie(technical: new TechnicalInfo { Height = 720 });

        var result = MediaFilterEngine.Evaluate(item, Array.Empty<MediaStreamInfo>(), config);

        Assert.Equal(FilterStatus.Rejected, result.Status);
    }

    // ---- CA3 — au moins une piste française, quel que soit l'ordre ----

    [Fact]
    public void Evaluate_AudioLanguage_KeptWhenFrenchTrackPresent_RegardlessOfPosition()
    {
        var config = new MediaFilterConfig { AudioLanguages = new List<string> { "fra" } };

        var frenchFirst  = MediaFilterEngine.Evaluate(Movie(), new[] { Audio("fra"), Audio("eng") }, config);
        var frenchSecond = MediaFilterEngine.Evaluate(Movie(), new[] { Audio("eng"), Audio("fra") }, config);
        var frenchThird  = MediaFilterEngine.Evaluate(Movie(),
            new[] { Audio("eng"), Audio("spa"), Audio("fra") }, config);

        Assert.Equal(FilterStatus.Kept, frenchFirst.Status);
        Assert.Equal(FilterStatus.Kept, frenchSecond.Status);
        Assert.Equal(FilterStatus.Kept, frenchThird.Status);
    }

    [Fact]
    public void Evaluate_AudioLanguage_RejectedWhenNoMatchingTrack()
    {
        var config = new MediaFilterConfig { AudioLanguages = new List<string> { "fra" } };

        var result = MediaFilterEngine.Evaluate(Movie(), new[] { Audio("eng"), Audio("spa") }, config);

        Assert.Equal(FilterStatus.Rejected, result.Status);
    }

    // ---- CA4 — équivalences ISO respectées côté configuration utilisateur ----

    [Theory]
    [InlineData("fr")]
    [InlineData("fre")]
    [InlineData("fra")]
    [InlineData("french")]
    [InlineData("French")]
    public void Evaluate_AudioLanguage_AcceptsAllIsoEquivalents_OfConfigValue(string configValue)
    {
        // AudioLanguages est alimenté par l'utilisateur (UI, texte libre) — le moteur doit
        // normaliser cette valeur via LanguageMatcher exactement comme il normalise les pistes,
        // sinon CA4 échoue pour toute saisie autre que la forme canonique "fra".
        var config = new MediaFilterConfig { AudioLanguages = new List<string> { configValue } };
        var streams = new[] { Audio("fra") }; // piste déjà normalisée par le mapper connecteur

        var result = MediaFilterEngine.Evaluate(Movie(), streams, config);

        Assert.Equal(FilterStatus.Kept, result.Status);
    }

    // ---- CA5 — ET entre critères de nature différente ----

    [Theory]
    [InlineData(720, "fra", FilterStatus.Rejected)]  // résolution insuffisante malgré langue OK
    [InlineData(1080, "eng", FilterStatus.Rejected)] // langue absente malgré résolution OK
    [InlineData(1080, "fra", FilterStatus.Kept)]     // les deux critères passent
    public void Evaluate_CombinesResolutionAndLanguage_WithAndSemantics(
        int height, string audioLang, FilterStatus expected)
    {
        var config = new MediaFilterConfig { MinHeight = 1080, AudioLanguages = new List<string> { "fra" } };
        var streams = new[] { Video(height), Audio(audioLang) };

        var result = MediaFilterEngine.Evaluate(Movie(), streams, config);

        Assert.Equal(expected, result.Status);
    }

    // ---- CA5 — OU à l'intérieur d'un même critère (plusieurs langues acceptées) ----

    [Theory]
    [InlineData("fra", FilterStatus.Kept)]
    [InlineData("eng", FilterStatus.Kept)]
    [InlineData("spa", FilterStatus.Rejected)]
    public void Evaluate_MultipleAudioLanguages_UseOrSemanticsWithinCriterion(
        string trackLanguage, FilterStatus expected)
    {
        var config = new MediaFilterConfig { AudioLanguages = new List<string> { "fra", "eng" } };
        var streams = new[] { Audio(trackLanguage) };

        var result = MediaFilterEngine.Evaluate(Movie(), streams, config);

        Assert.Equal(expected, result.Status);
    }

    // ---- Sous-titres forcés ----

    [Fact]
    public void Evaluate_SubtitleLanguage_ForcedTrackCounts_ByDefault()
    {
        var config = new MediaFilterConfig
        {
            SubtitleLanguages = new List<string> { "fra" },
            IgnoreForcedSubtitles = false
        };
        var streams = new[] { Subtitle("fra", isForced: true) };

        var result = MediaFilterEngine.Evaluate(Movie(), streams, config);

        Assert.Equal(FilterStatus.Kept, result.Status);
    }

    [Fact]
    public void Evaluate_SubtitleLanguage_ForcedTrackExcluded_WhenIgnoreForcedSubtitlesIsSet()
    {
        var config = new MediaFilterConfig
        {
            SubtitleLanguages = new List<string> { "fra" },
            IgnoreForcedSubtitles = true
        };
        // Seule piste disponible = forcée → doit être exclue du test, donc plus aucune piste ne matche.
        var streams = new[] { Subtitle("fra", isForced: true) };

        var result = MediaFilterEngine.Evaluate(Movie(), streams, config);

        Assert.Equal(FilterStatus.Rejected, result.Status);
    }

    [Fact]
    public void Evaluate_SubtitleLanguage_NonForcedTrackStillMatches_WhenIgnoreForcedSubtitlesIsSet()
    {
        var config = new MediaFilterConfig
        {
            SubtitleLanguages = new List<string> { "fra" },
            IgnoreForcedSubtitles = true
        };
        var streams = new[] { Subtitle("fra", isForced: true), Subtitle("fra", isForced: false) };

        var result = MediaFilterEngine.Evaluate(Movie(), streams, config);

        Assert.Equal(FilterStatus.Kept, result.Status);
    }

    // ---- CA6 — fail-open sur information manquante ----

    [Fact]
    public void Evaluate_NullTechnicalInfo_And_EmptyStreams_IsKeptUnknownInfo()
    {
        var config = new MediaFilterConfig { MinHeight = 1080 };
        var item = Movie(technical: null);

        var result = MediaFilterEngine.Evaluate(item, Array.Empty<MediaStreamInfo>(), config);

        Assert.Equal(FilterStatus.KeptUnknownInfo, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Reason)); // le décompte agrégé en dépend côté SyncService
    }

    [Fact]
    public void Evaluate_EmptyStreamsList_ForLanguageRule_IsKeptUnknownInfo()
    {
        // Une règle de langue n'a pas de repli scalaire (contrairement à la résolution) : une
        // liste de pistes vide signifie "information indisponible", pas "aucune langue ne matche".
        var config = new MediaFilterConfig { AudioLanguages = new List<string> { "fra" } };

        var result = MediaFilterEngine.Evaluate(Movie(), Array.Empty<MediaStreamInfo>(), config);

        Assert.Equal(FilterStatus.KeptUnknownInfo, result.Status);
    }

    // ---- CA7 — portée Movie/Episode uniquement ----

    [Theory]
    [InlineData(MediaType.Music)]
    [InlineData(MediaType.AudioBook)]
    [InlineData(MediaType.Book)]
    [InlineData(MediaType.Photo)]
    public void Evaluate_OutOfScopeMediaTypes_AreKeptWithoutEvaluation(MediaType type)
    {
        // Règle volontairement stricte pour prouver qu'elle N'EST PAS appliquée à ces types :
        // si elle l'était, cet item serait rejeté (aucune piste vidéo, ni TechnicalInfo, 4K exigé).
        var config = new MediaFilterConfig { MinHeight = 2160 };
        var item = new MediaItem { RemoteId = "x1", Title = "Other", Type = type };

        var result = MediaFilterEngine.Evaluate(item, Array.Empty<MediaStreamInfo>(), config);

        Assert.Equal(FilterStatus.Kept, result.Status);
    }

    [Fact]
    public void Evaluate_Episode_IsEvaluated_SameAsMovie()
    {
        var config = new MediaFilterConfig { MinHeight = 1080 };
        var streams = new[] { Video(720) };

        var result = MediaFilterEngine.Evaluate(Episode(), streams, config);

        Assert.Equal(FilterStatus.Rejected, result.Status);
    }
}
