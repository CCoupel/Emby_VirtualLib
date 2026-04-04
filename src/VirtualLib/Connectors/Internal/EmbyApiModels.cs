using System.Text.Json.Serialization;

namespace VirtualLib.Connectors.Internal;

internal sealed class EmbySystemInfo
{
    [JsonPropertyName("Version")]
    public string? Version { get; init; }

    [JsonPropertyName("ServerName")]
    public string? ServerName { get; init; }
}

internal sealed class EmbyLibraryFolder
{
    [JsonPropertyName("ItemId")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("CollectionType")]
    public string? CollectionType { get; init; }
}

internal sealed class EmbyItemsResponse
{
    [JsonPropertyName("Items")]
    public List<EmbyItem> Items { get; init; } = new();

    [JsonPropertyName("TotalRecordCount")]
    public int TotalRecordCount { get; init; }

    [JsonPropertyName("StartIndex")]
    public int StartIndex { get; init; }
}

internal sealed class EmbyItem
{
    [JsonPropertyName("Id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("Type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("ProductionYear")]
    public int? ProductionYear { get; init; }

    [JsonPropertyName("Overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("Genres")]
    public List<string>? Genres { get; init; }

    [JsonPropertyName("Studios")]
    public List<EmbyStudio>? Studios { get; init; }

    [JsonPropertyName("Tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("ProviderIds")]
    public Dictionary<string, string>? ProviderIds { get; init; }

    [JsonPropertyName("DateCreated")]
    public DateTime? DateCreated { get; init; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; init; }

    [JsonPropertyName("CommunityRating")]
    public float? CommunityRating { get; init; }

    [JsonPropertyName("OfficialRating")]
    public string? OfficialRating { get; init; }

    [JsonPropertyName("SeriesName")]
    public string? SeriesName { get; init; }

    [JsonPropertyName("SeriesId")]
    public string? SeriesId { get; init; }

    [JsonPropertyName("SeasonId")]
    public string? SeasonId { get; init; }

    [JsonPropertyName("ParentIndexNumber")]
    public int? ParentIndexNumber { get; init; }

    [JsonPropertyName("IndexNumber")]
    public int? IndexNumber { get; init; }

    // Audio / AudioBook fields
    [JsonPropertyName("Album")]
    public string? Album { get; init; }

    [JsonPropertyName("AlbumId")]
    public string? AlbumId { get; init; }

    [JsonPropertyName("AlbumArtist")]
    public string? AlbumArtist { get; init; }

    [JsonPropertyName("ImageTags")]
    public Dictionary<string, string>? ImageTags { get; init; }

    [JsonPropertyName("BackdropImageTags")]
    public List<string>? BackdropImageTags { get; init; }

    [JsonPropertyName("People")]
    public List<EmbyPerson>? People { get; init; }

    [JsonPropertyName("RemoteTrailers")]
    public List<EmbyRemoteTrailer>? RemoteTrailers { get; init; }

    [JsonPropertyName("Taglines")]
    public List<string>? Taglines { get; init; }

    [JsonPropertyName("MediaSources")]
    public List<EmbyMediaSource>? MediaSources { get; init; }

    [JsonPropertyName("UserData")]
    public EmbyUserData? UserData { get; init; }
}

internal sealed class EmbyUserData
{
    [JsonPropertyName("IsFavorite")]
    public bool IsFavorite { get; init; }

    [JsonPropertyName("Played")]
    public bool Played { get; init; }

    [JsonPropertyName("PlayCount")]
    public int PlayCount { get; init; }

    [JsonPropertyName("LastPlayedDate")]
    public DateTime? LastPlayedDate { get; init; }

    [JsonPropertyName("PlaybackPositionTicks")]
    public long PlaybackPositionTicks { get; init; }
}

internal sealed class EmbyMediaSource
{
    [JsonPropertyName("Size")]
    public long? Size { get; init; }

    [JsonPropertyName("Bitrate")]
    public int? Bitrate { get; init; }

    [JsonPropertyName("Container")]
    public string? Container { get; init; }

    [JsonPropertyName("MediaStreams")]
    public List<EmbyMediaStream>? MediaStreams { get; init; }
}

internal sealed class EmbyMediaStream
{
    [JsonPropertyName("Type")]
    public string? Type { get; init; }

    [JsonPropertyName("Codec")]
    public string? Codec { get; init; }

    [JsonPropertyName("Width")]
    public int? Width { get; init; }

    [JsonPropertyName("Height")]
    public int? Height { get; init; }

    [JsonPropertyName("BitRate")]
    public int? BitRate { get; init; }

    [JsonPropertyName("Channels")]
    public int? Channels { get; init; }

    [JsonPropertyName("SampleRate")]
    public int? SampleRate { get; init; }
}

internal sealed class EmbyStudio
{
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;
}

internal sealed class EmbyPerson
{
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("Role")]
    public string? Role { get; init; }

    [JsonPropertyName("Type")]
    public string? Type { get; init; }
}

internal sealed class EmbyRemoteTrailer
{
    [JsonPropertyName("Url")]
    public string? Url { get; init; }
}

internal sealed class EmbyUser
{
    [JsonPropertyName("Id")]
    public string Id { get; init; } = string.Empty;
}

internal sealed class EmbyAuthResult
{
    [JsonPropertyName("AccessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("User")]
    public EmbyUser? User { get; init; }

    [JsonPropertyName("SessionInfo")]
    public EmbySessionInfo? SessionInfo { get; init; }
}

internal sealed class EmbySessionInfo
{
    [JsonPropertyName("Id")]
    public string Id { get; init; } = string.Empty;
}
