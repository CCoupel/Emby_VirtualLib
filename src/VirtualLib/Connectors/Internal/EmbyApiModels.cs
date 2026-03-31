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

    [JsonPropertyName("ParentIndexNumber")]
    public int? ParentIndexNumber { get; init; }

    [JsonPropertyName("IndexNumber")]
    public int? IndexNumber { get; init; }

    [JsonPropertyName("ImageTags")]
    public Dictionary<string, string>? ImageTags { get; init; }

    [JsonPropertyName("BackdropImageTags")]
    public List<string>? BackdropImageTags { get; init; }
}

internal sealed class EmbyStudio
{
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;
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
