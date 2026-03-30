namespace VirtualLib.Core.Models;

public sealed class ConnectorTestResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ServerVersion { get; init; }

    public static ConnectorTestResult Ok(string version) =>
        new() { Success = true, ServerVersion = version };

    public static ConnectorTestResult Fail(string error) =>
        new() { Success = false, ErrorMessage = error };
}
