namespace VirtualLib.Core.Models;

public sealed class MediaMetadata : MediaItem
{
    public string? Overview { get; init; }
    public float? CommunityRating { get; init; }
    public int? RuntimeMinutes { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Studios { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? OfficialRating { get; init; }
    public IReadOnlyList<PersonInfo> Cast { get; init; } = Array.Empty<PersonInfo>();
    public IReadOnlyList<string> Directors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Writers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();
    public string? Tagline { get; init; }
    public string? TrailerUrl { get; init; }
}
