using System.Collections.Concurrent;

namespace VirtualLib.Core;

public enum LibrarySyncStatus { Pending, RunningPhase1, RunningPhase2, Done, Failed }

/// <summary>Per-library sync progress tracked during a parallel sync run.</summary>
public sealed class LibrarySyncEntry
{
    public string ConnectorId   { get; init; } = string.Empty;
    public string ConnectorName { get; init; } = string.Empty;
    public string LibraryId     { get; init; } = string.Empty;
    public string LibraryName   { get; init; } = string.Empty;
    public string MediaType     { get; init; } = string.Empty;

    private int _status = (int)LibrarySyncStatus.Pending;
    private int _p1Done, _p1Total, _p2Done, _p2Total;

    public LibrarySyncStatus Status
    {
        get => (LibrarySyncStatus)Volatile.Read(ref _status);
        set => Volatile.Write(ref _status, (int)value);
    }
    public int Phase1Done  { get => Volatile.Read(ref _p1Done);  set => Volatile.Write(ref _p1Done,  value); }
    public int Phase1Total { get => Volatile.Read(ref _p1Total); set => Volatile.Write(ref _p1Total, value); }
    public int Phase2Done  { get => Volatile.Read(ref _p2Done);  set => Volatile.Write(ref _p2Done,  value); }
    public int Phase2Total { get => Volatile.Read(ref _p2Total); set => Volatile.Write(ref _p2Total, value); }

    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Thread-safe global sync state — prevents concurrent syncs and exposes
/// per-library parallel progress to the config page (survives page refresh).
/// </summary>
public static class SyncState
{
    private static int _isSyncing;
    private static readonly ConcurrentDictionary<string, LibrarySyncEntry> _libraries = new();

    public static bool     IsSyncing  => Volatile.Read(ref _isSyncing) == 1;
    public static DateTime? StartedAt { get; private set; }
    public static List<SyncResult>? LastResults { get; private set; }

    public static ICollection<LibrarySyncEntry> Libraries => _libraries.Values;

    public static string MakeKey(string connectorId, string libraryId) =>
        $"{connectorId}::{libraryId}";

    /// <summary>Attempt to acquire the sync lock. Returns false if already running.</summary>
    public static bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0) return false;
        _libraries.Clear();
        StartedAt   = DateTime.UtcNow;
        LastResults = null;
        return true;
    }

    public static void RegisterLibrary(
        string connectorId, string connectorName,
        string libraryId,   string libraryName,
        string mediaType)
    {
        _libraries[MakeKey(connectorId, libraryId)] = new LibrarySyncEntry
        {
            ConnectorId   = connectorId,
            ConnectorName = connectorName,
            LibraryId     = libraryId,
            LibraryName   = libraryName,
            MediaType     = mediaType
        };
    }

    public static void UpdatePhase1(string connectorId, string libraryId, int done, int total)
    {
        if (_libraries.TryGetValue(MakeKey(connectorId, libraryId), out var e))
        {
            e.Status     = LibrarySyncStatus.RunningPhase1;
            e.Phase1Done  = done;
            e.Phase1Total = total;
        }
    }

    public static void Phase2Start(string connectorId, string libraryId, int total)
    {
        if (_libraries.TryGetValue(MakeKey(connectorId, libraryId), out var e))
        {
            e.Status     = LibrarySyncStatus.RunningPhase2;
            e.Phase2Total = total;
            e.Phase2Done  = 0;
        }
    }

    public static void UpdatePhase2(string connectorId, string libraryId, int done, int total)
    {
        if (_libraries.TryGetValue(MakeKey(connectorId, libraryId), out var e))
        {
            e.Phase2Done  = done;
            e.Phase2Total = total;
        }
    }

    public static void MarkDone(string connectorId, string libraryId)
    {
        if (_libraries.TryGetValue(MakeKey(connectorId, libraryId), out var e))
            e.Status = LibrarySyncStatus.Done;
    }

    public static void MarkFailed(string connectorId, string libraryId, string error)
    {
        if (_libraries.TryGetValue(MakeKey(connectorId, libraryId), out var e))
        {
            e.Status       = LibrarySyncStatus.Failed;
            e.ErrorMessage = error;
        }
    }

    public static void Finish(List<SyncResult>? results = null)
    {
        LastResults = results;
        StartedAt   = null;
        Volatile.Write(ref _isSyncing, 0);
    }
}
