using VirtualLib.Core.Models;
using Xunit;

namespace VirtualLib.Tests;

/// <summary>
/// Régression urgente (#44) : un ConnectorConfig désérialisé depuis un XML de configuration
/// antérieur à #44 (donc sans nœud MediaFilter/AudioLanguages/SubtitleLanguages) ne doit jamais
/// lever de NullReferenceException lors de la lecture de <see cref="MediaFilterConfig.IsActive"/>.
/// Le comportement d'IXmlSerializer d'Emby sur un type complexe absent du XML n'est pas garanti
/// respecter les initialiseurs de propriété C# ("= new()") — ces tests couvrent le cas où les
/// listes se retrouvent null au runtime, quelle qu'en soit la cause exacte côté désérialisation.
/// </summary>
public class MediaFilterConfigTests
{
    [Fact]
    public void IsActive_DoesNotThrow_WhenAudioLanguagesIsNull()
    {
        var filter = new MediaFilterConfig
        {
            MinHeight = 0,
            AudioLanguages = null!,
            SubtitleLanguages = new List<string>()
        };

        Assert.False(filter.IsActive);
    }

    [Fact]
    public void IsActive_DoesNotThrow_WhenSubtitleLanguagesIsNull()
    {
        var filter = new MediaFilterConfig
        {
            MinHeight = 0,
            AudioLanguages = new List<string>(),
            SubtitleLanguages = null!
        };

        Assert.False(filter.IsActive);
    }

    [Fact]
    public void IsActive_DoesNotThrow_WhenBothListsAreNull()
    {
        var filter = new MediaFilterConfig
        {
            MinHeight = 0,
            AudioLanguages = null!,
            SubtitleLanguages = null!
        };

        Assert.False(filter.IsActive);
    }

    [Fact]
    public void IsActive_StillTrue_WhenMinHeightSetAndListsAreNull()
    {
        // MinHeight alone must still activate the filter even with null lists elsewhere —
        // the null-safety fix must not silently disable a legitimately configured rule.
        var filter = new MediaFilterConfig
        {
            MinHeight = 1080,
            AudioLanguages = null!,
            SubtitleLanguages = null!
        };

        Assert.True(filter.IsActive);
    }

    [Fact]
    public void ConnectorConfig_MediaFilter_DefaultsToNonNull_ForNewInstances()
    {
        // Documents the expected default for a freshly-constructed ConnectorConfig (Post/new
        // connector path) — MediaFilter is never null when built via `new()` in C# code. The
        // risk this whole test file guards against is specific to XML deserialization of
        // pre-#44 connectors, not to normal object construction.
        var config = new ConnectorConfig();
        Assert.NotNull(config.MediaFilter);
        Assert.False(config.MediaFilter.IsActive);
    }
}
