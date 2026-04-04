namespace VirtualLib.Core;

/// <summary>
/// Thread-safe global sync state — prevents concurrent syncs and exposes
/// granular progress to the config page (survives page refresh).
/// </summary>
public static class SyncState
{
    private static int _isSyncing;
    private static int _doneConnectors;
    private static int _doneLibraries;
    private static int _doneItems;
    private static int _totalItems;
    private static string _prevLibraryName = "";

    public static bool IsSyncing    => Volatile.Read(ref _isSyncing) == 1;
    public static string Message    { get; private set; } = string.Empty;
    public static DateTime? StartedAt { get; private set; }

    // Connector-level
    public static string CurrentConnectorName { get; private set; } = string.Empty;
    public static int TotalConnectors { get; private set; }
    public static int DoneConnectors  => Volatile.Read(ref _doneConnectors);

    // Library-level (within current connector)
    public static string CurrentLibraryName { get; private set; } = string.Empty;
    public static int TotalLibraries { get; private set; }
    public static int DoneLibraries  => Volatile.Read(ref _doneLibraries);

    // Item-level (within current library)
    public static int TotalItems => Volatile.Read(ref _totalItems);
    public static int DoneItems  => Volatile.Read(ref _doneItems);

    /// <summary>Results of the last completed sync.</summary>
    public static List<SyncResult>? LastResults { get; private set; }

    /// <summary>Attempt to acquire the sync lock. Returns false if already running.</summary>
    public static int Phase { get; private set; } = 1;

    public static bool TryStart(string message, int totalConnectors = 1)
    {
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0)
            return false;
        Phase    = 1;
        Message   = message;
        StartedAt = DateTime.UtcNow;
        TotalConnectors = totalConnectors;
        Volatile.Write(ref _doneConnectors, 0);
        TotalLibraries = 0;
        Volatile.Write(ref _doneLibraries, 0);
        Volatile.Write(ref _totalItems, 0);
        Volatile.Write(ref _doneItems, 0);
        _prevLibraryName = "";
        CurrentConnectorName = "";
        CurrentLibraryName = "";
        return true;
    }

    /// <summary>Transitions to phase 2 (metadata injection), resetting progress counters.</summary>
    public static void StartPhase2(int totalConnectors, int totalLibraries)
    {
        Phase = 2;
        Message = "Injecting metadata…";
        TotalConnectors = totalConnectors;
        TotalLibraries  = totalLibraries;
        Volatile.Write(ref _doneConnectors, 0);
        Volatile.Write(ref _doneLibraries, 0);
        Volatile.Write(ref _totalItems, 0);
        Volatile.Write(ref _doneItems, 0);
        _prevLibraryName = "";
        CurrentConnectorName = "";
        CurrentLibraryName = "";
    }

    /// <summary>Call just before SyncConnectorAsync for each connector.</summary>
    public static void ConnectorStarted(string connectorName, int totalLibraries)
    {
        CurrentConnectorName = connectorName;
        Message = $"Syncing {connectorName}…";
        TotalLibraries = totalLibraries;
        Volatile.Write(ref _doneLibraries, 0);
        Volatile.Write(ref _totalItems, 0);
        Volatile.Write(ref _doneItems, 0);
        _prevLibraryName = "";
        CurrentLibraryName = "";
    }

    /// <summary>Called via IProgress&lt;SyncProgress&gt; for each item processed.</summary>
    public static void ReportItemProgress(SyncProgress p)
    {
        // Detect library transition
        if (p.LibraryName != _prevLibraryName)
        {
            if (!string.IsNullOrEmpty(_prevLibraryName))
                Interlocked.Increment(ref _doneLibraries);
            _prevLibraryName = p.LibraryName;
            CurrentLibraryName = p.LibraryName;
        }
        Volatile.Write(ref _totalItems, p.Total);
        Volatile.Write(ref _doneItems, p.Current);
    }

    /// <summary>Call after each connector finishes.</summary>
    public static void ConnectorCompleted() => Interlocked.Increment(ref _doneConnectors);

    public static void Finish(List<SyncResult>? results = null)
    {
        LastResults = results;
        Message     = string.Empty;
        StartedAt   = null;
        Volatile.Write(ref _isSyncing, 0);
    }
}
