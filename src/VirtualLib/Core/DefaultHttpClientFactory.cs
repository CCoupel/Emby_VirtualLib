namespace VirtualLib.Core;

/// <summary>
/// Implémentation minimale de IHttpClientFactory pour les contextes sans DI.
/// </summary>
internal sealed class DefaultHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new HttpClient();
}
