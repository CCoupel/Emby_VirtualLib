namespace VirtualLib.Core.Models;

public class MediaItem
{
    public string RemoteId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public MediaType Type { get; init; }
    public int? Year { get; init; }

    // Series only
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }

    // External IDs
    public string? ImdbId { get; init; }
    public string? TmdbId { get; init; }
    public string? TvdbId { get; init; }

    public DateTime? DateAdded { get; init; }

    /// <summary>Duration in 100-nanosecond ticks (same unit as Emby's RunTimeTicks).</summary>
    public long? RuntimeTicks { get; init; }

    /// <summary>Album artists / book authors — used to group audiobook chapters into a container.</summary>
    public IReadOnlyList<string> AlbumArtists { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ArtworkType> AvailableArtwork { get; init; } = Array.Empty<ArtworkType>();
}
