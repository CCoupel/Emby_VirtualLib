using System.Text.Json.Serialization;

namespace VirtualLib.Core.Cache;

/// <summary>
/// A contiguous byte range stored as a single .bin file.
/// Segments are created at the configured flush interval and merged when adjacent.
/// </summary>
public sealed class CachedSegment
{
    public long   Start    { get; set; }
    public long   Length   { get; set; }
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Index of the first complete chunk (at manifest ChunkSize) fully covered by this segment.
    /// -1 if the segment covers no complete chunk.
    /// </summary>
    public int FirstCoveredChunk { get; set; } = -1;

    /// <summary>
    /// Index of the last complete chunk fully covered by this segment (inclusive).
    /// -1 if the segment covers no complete chunk.
    /// </summary>
    public int LastCoveredChunk { get; set; } = -1;

    /// <summary>Exclusive end offset (Start + Length).</summary>
    [JsonIgnore]
    public long End => Start + Length;

    /// <summary>Exclusive end chunk index (LastCoveredChunk + 1), or -1 if no chunk covered.</summary>
    [JsonIgnore]
    public int EndChunk => LastCoveredChunk >= 0 ? LastCoveredChunk + 1 : -1;
}

/// <summary>
/// Tracks the download state of a cached media item as a sorted list of contiguous segments.
///
/// Layout on disk:
///   {cacheRoot}/{connectorId}/{itemId}/
///     manifest.json                              ← this model (written atomically via .tmp → rename)
///     seg_00000000000000000000_&lt;guid&gt;.bin   ← a contiguous byte range
///     seg_00000000002097152000_&lt;guid&gt;.bin   ← another range — merged when adjacent
///
/// Invariants:
///   - A .bin file is always complete (written via .tmp → rename).
///   - Segments is always sorted by Start with no overlaps.
///   - Adjacent segments are merged into a single file automatically.
///   - When fully cached, Segments contains exactly one entry spanning [0, TotalSize).
/// </summary>
public sealed class ChunkManifest
{
    public string ItemId      { get; set; } = string.Empty;
    public string ConnectorId { get; set; } = string.Empty;
    public long   TotalSize   { get; set; } = -1;

    /// <summary>
    /// Flush interval used when this manifest was created.
    /// Stored for reference only — changing it does not invalidate existing segments.
    /// Used to compute FirstCoveredChunk / LastCoveredChunk on each segment.
    /// </summary>
    public int    ChunkSize   { get; set; } = 2 * 1024 * 1024;

    public string ContentType { get; set; } = "application/octet-stream";
    public string SourceUrl   { get; set; } = string.Empty;

    /// <summary>Sorted ascending by Start; no two segments overlap.</summary>
    public List<CachedSegment> Segments { get; set; } = new();

    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessAt { get; set; } = DateTime.UtcNow;

    // ── Computed ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Total number of chunks at the configured ChunkSize, including the last partial chunk.
    /// ceil(TotalSize / ChunkSize) — the last chunk may be smaller than ChunkSize.
    /// -1 if TotalSize or ChunkSize is unknown.
    /// </summary>
    public int TotalChunks => TotalSize > 0 && ChunkSize > 0
        ? (int)Math.Ceiling((double)TotalSize / ChunkSize)
        : -1;

    /// <summary>
    /// Percentage of the file currently covered by cached segments (0–100).
    /// Computed from segment lengths vs TotalSize. Displayed in manifest.json for monitoring.
    /// </summary>
    public double CachedPercent => TotalSize > 0
        ? Math.Round(Segments.Sum(s => s.Length) * 100.0 / TotalSize, 1)
        : 0;

    [JsonIgnore]
    public bool IsComplete =>
        TotalSize > 0
        && Segments.Count == 1
        && Segments[0].Start == 0
        && Segments[0].Length == TotalSize;

    /// <summary>
    /// Returns true if a single segment fully covers [<paramref name="rangeStart"/>,
    /// <paramref name="inclusiveRangeEnd"/>] (both bounds inclusive).
    /// </summary>
    public bool IsRangeCached(long rangeStart, long inclusiveRangeEnd)
    {
        foreach (var seg in Segments)   // sorted ascending
        {
            if (seg.Start <= rangeStart && seg.End > inclusiveRangeEnd) return true;
            if (seg.Start > rangeStart) break;
        }
        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="rangeStart"/> falls inside any existing segment.
    /// Used to detect overlapping writes before committing a new segment.
    /// </summary>
    public bool StartsInsideExistingSegment(long rangeStart)
        => Segments.Any(s => s.Start <= rangeStart && s.End > rangeStart);

    // ── Chunk coverage helpers ─────────────────────────────────────────────

    /// <summary>
    /// Recomputes <see cref="CachedSegment.FirstCoveredChunk"/> and
    /// <see cref="CachedSegment.LastCoveredChunk"/> for every segment.
    /// Call after adding or merging segments.
    /// </summary>
    public void RefreshChunkCoverage()
    {
        if (ChunkSize <= 0 || TotalSize <= 0) return;
        foreach (var seg in Segments)
            ComputeChunkCoverage(seg, ChunkSize, TotalSize);
    }

    internal static void ComputeChunkCoverage(CachedSegment seg, int chunkSize, long totalSize)
    {
        // First chunk whose entire byte range falls within [seg.Start, seg.End)
        // Chunk i covers [i*chunkSize, min((i+1)*chunkSize, totalSize))
        var firstChunk = (int)((seg.Start + chunkSize - 1) / chunkSize);

        int last = -1;
        for (var i = firstChunk; ; i++)
        {
            if ((long)i * chunkSize >= totalSize) break;  // chunk start is beyond EOF — phantom chunk
            var chunkEnd = Math.Min((long)(i + 1) * chunkSize, totalSize);  // exclusive end of chunk i
            if (chunkEnd > seg.End) break;
            last = i;
            if (chunkEnd >= totalSize) break;   // was the last chunk of the file
        }

        seg.FirstCoveredChunk = last >= 0 ? firstChunk : -1;
        seg.LastCoveredChunk  = last;
    }
}
