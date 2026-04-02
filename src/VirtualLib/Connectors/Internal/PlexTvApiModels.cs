using System.Text.Json.Serialization;

namespace VirtualLib.Connectors.Internal;

// plex.tv /api/v2/resources response
internal sealed class PlexTvResource
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("clientIdentifier")]
    public string ClientIdentifier { get; set; } = string.Empty;

    [JsonPropertyName("provides")]
    public string Provides { get; set; } = string.Empty;

    /// <summary>
    /// Per-server access token returned by plex.tv — use this to authenticate
    /// with the PMS directly (differs from the global auth token for shared servers).
    /// </summary>
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("connections")]
    public List<PlexTvConnection> Connections { get; set; } = new();
}

internal sealed class PlexTvConnection
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = string.Empty;

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("local")]
    public bool Local { get; set; }

    [JsonPropertyName("relay")]
    public bool Relay { get; set; }

    [JsonPropertyName("IPv6")]
    public bool IPv6 { get; set; }
}

// plex.tv /users/sign_in.json response (partial)
internal sealed class PlexTvSignInResponse
{
    [JsonPropertyName("user")]
    public PlexTvUser? User { get; set; }
}

internal sealed class PlexTvUser
{
    [JsonPropertyName("authToken")]
    public string AuthToken { get; set; } = string.Empty;
}
