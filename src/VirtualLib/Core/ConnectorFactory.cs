using Microsoft.Extensions.Logging;
using VirtualLib.Connectors;

namespace VirtualLib.Core;

public interface IConnectorFactory
{
    IMediaServerConnector Create(ConnectorConfig config);
}

public sealed class ConnectorFactory : IConnectorFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public ConnectorFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public IMediaServerConnector Create(ConnectorConfig config) => config.ServerType switch
    {
        ServerTypes.Emby or ServerTypes.Jellyfin => new EmbyConnector(
            config,
            _httpClientFactory,
            _loggerFactory.CreateLogger<EmbyConnector>()),
        ServerTypes.Plex => new PlexConnector(
            config,
            _httpClientFactory,
            _loggerFactory.CreateLogger<PlexConnector>()),
        ServerTypes.PlexTV => new PlexTvConnector(
            config,
            _httpClientFactory,
            _loggerFactory,
            _loggerFactory.CreateLogger<PlexTvConnector>()),
        _ => throw new NotSupportedException($"Server type '{config.ServerType}' is not supported")
    };
}

public static class ServerTypes
{
    public const string Emby     = "Emby";
    public const string Jellyfin = "Jellyfin";
    public const string Plex     = "Plex";
    public const string PlexTV   = "PlexTV";
}
