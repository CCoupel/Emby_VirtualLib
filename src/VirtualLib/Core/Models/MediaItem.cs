namespace VirtualLib.Core.Models;

public class MediaItem
{
    public string RemoteId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public MediaType Type { get; init; }
    public int? Year { get; init; }

    // Series only
    public string? SeriesId { get; init; }
    public string? SeasonId { get; init; }
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

    // User state (played / favorite / resume) — synced from the remote server for the authenticated user
    public bool      IsPlayed               { get; init; }
    public bool      IsFavorite             { get; init; }
    public int       PlayCount              { get; init; }
    public DateTime? LastPlayedDate         { get; init; }
    /// <summary>Resume position in 100-ns ticks (same unit as Emby's PlaybackPositionTicks).</summary>
    public long      PlaybackPositionTicks  { get; init; }

    /// <summary>Album artists / book authors — used to group audiobook chapters into a container.</summary>
    public IReadOnlyList<string> AlbumArtists { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ArtworkType> AvailableArtwork { get; init; } = Array.Empty<ArtworkType>();

    /// <summary>Technical stream info fetched from the remote server's MediaSources.</summary>
    public TechnicalInfo? Technical { get; init; }
}

/// <summary>
/// Technical metadata from the remote server's MediaSources (already probed by the source).
/// Injected directly into Emby DB items via the polling loop, bypassing the deferred ffprobe
/// that Emby would otherwise run only at first playback for .strm files.
/// </summary>
public sealed class TechnicalInfo
{
    public long?   Size            { get; init; }  // File size in bytes
    public int?    Bitrate         { get; init; }  // Total bitrate in bps
    public string? Container       { get; init; }  // Container format (mkv, mp4, avi…)
    public int?    Width           { get; init; }  // Video width in pixels
    public int?    Height          { get; init; }  // Video height in pixels
    public string? VideoCodec      { get; init; }  // Video codec (h264, hevc…)
    public string? AudioCodec      { get; init; }  // Primary audio codec (ac3, aac…)
    public int?    AudioChannels   { get; init; }  // Audio channel count
    public int?    AudioSampleRate { get; init; }  // Audio sample rate in Hz
}
