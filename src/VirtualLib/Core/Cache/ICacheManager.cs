namespace VirtualLib.Core.Cache;

/// <summary>
/// Manages a segment-based local cache for proxied media streams.
///
/// Concurrency guarantees:
///   - Orphaned .tmp files are removed on <see cref="InitializeAsync"/>.
///   - Segment files are written atomically (.tmp → rename); a .bin file is always complete.
///   - The in-memory manifest is the source of truth, flushed to disk under a per-item
///     SemaphoreSlim after each segment acquisition.
///   - Adjacent segments are merged (files concatenated) automatically so that sequential
///     playback converges toward a single file regardless of chunk-size configuration.
///   - A double-check inside the manifest lock prevents duplicate writes for the same range.
/// </summary>
public interface ICacheManager
{
    /// <summary>
    /// Creates or returns the existing manifest for an item.
    /// Call this as soon as TotalSize and ContentType are known (from upstream response headers).
    /// Idempotent: subsequent calls with the same key return the existing manifest unchanged.
    /// </summary>
    Task<ChunkManifest> EnsureManifestAsync(
        string connectorId,
        string itemId,
        long   totalSize,
        string contentType,
        string sourceUrl,
        int    chunkSizeBytes = 2 * 1024 * 1024,
        CancellationToken ct = default);

    /// <summary>Returns the in-memory (or disk-loaded) manifest, or null if not cached.</summary>
    Task<ChunkManifest?> GetManifestAsync(string connectorId, string itemId);

    /// <summary>
    /// True if a single cached segment fully covers [rangeStart, rangeEnd] (both inclusive).
    /// Returns false when manifest is null or TotalSize is unknown.
    /// </summary>
    bool IsRangeCached(ChunkManifest? manifest, long rangeStart, long rangeEnd);

    /// <summary>
    /// Serves a byte range entirely from a cached segment file.
    /// <see cref="IsRangeCached"/> must be true for the given range.
    /// </summary>
    Task ServeCachedRangeAsync(
        ChunkManifest     manifest,
        string            connectorId,
        string            itemId,
        long              rangeStart,
        long              rangeEnd,
        Stream            destination,
        CancellationToken ct = default);

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> while caching
    /// the download as a series of segments (write-through streaming).
    /// Every <paramref name="flushIntervalBytes"/> a segment is committed to disk so that
    /// partial downloads are preserved even if the client disconnects early.
    /// Consecutive segments are merged automatically into a single file.
    /// </summary>
    Task CopyWithCacheAsync(
        Stream            source,
        Stream            destination,
        ChunkManifest     manifest,
        string            connectorId,
        string            itemId,
        long              startOffset,
        int               flushIntervalBytes = 2 * 1024 * 1024,
        CancellationToken ct = default);

    /// <summary>
    /// Validates the on-disk state for a single item:
    /// <list type="bullet">
    ///   <item>Removes segments whose .bin file is missing (phantom segments).</item>
    ///   <item>Deletes .bin files that are not referenced by any segment (orphans).</item>
    ///   <item>Flushes the manifest if any changes were made.</item>
    /// </list>
    /// Idempotent and safe to call concurrently.
    /// </summary>
    Task ValidateItemAsync(string connectorId, string itemId, CancellationToken ct = default);

    /// <summary>
    /// Waits until the pending segment at <paramref name="segStart"/> is committed or discarded.
    /// Returns true if committed (data is now in cache), false if discarded or no pending segment.
    /// Returns immediately with false if no pending segment exists at that offset.
    /// </summary>
    Task<bool> WaitForPendingSegmentAsync(
        string connectorId, string itemId, long segStart, CancellationToken ct = default);

    /// <summary>Removes all cache files and the manifest for the given item.</summary>
    Task InvalidateAsync(string connectorId, string itemId);

    /// <summary>
    /// Copies the single complete segment file to <paramref name="destPath"/>.
    /// Requires <see cref="ChunkManifest.IsComplete"/> == true.
    /// </summary>
    Task<string> PromoteToFileAsync(
        string            connectorId,
        string            itemId,
        string            destPath,
        CancellationToken ct = default);

    /// <summary>
    /// Startup routine: deletes orphaned .tmp files and validates that every segment
    /// referenced in a manifest has a corresponding .bin file on disk.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    CacheStats GetStats();
}

public sealed class CacheStats
{
    public int  TotalItems     { get; init; }
    public int  CompleteItems  { get; init; }
    public long TotalSizeBytes { get; init; }
}
