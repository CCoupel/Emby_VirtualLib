namespace VirtualLib.Core.Models;

public sealed class RemoteLibrary
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public LibraryType Type { get; init; }
}
