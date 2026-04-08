using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtualLib.Core.Cache;

/// <summary>
/// Segment-based local cache for proxied media streams.
/// See <see cref="ICacheManager"/> for the full concurrency contract.
/// </summary>
public sealed class CacheManager : ICacheManager
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly string _cacheRoot;
    private readonly ILogger<CacheManager> _logger;

    private readonly ConcurrentDictionary<string, ChunkManifest> _manifests     = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _manifestLocks = new();

    /// <summary>Signals waiters when a segment being downloaded by another client is committed.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingSegments = new();

    /// <summary>Number of active waiters per pending segment key (used for the 50% finish rule).</summary>
    private readonly ConcurrentDictionary<string, int> _pendingWaiters = new();

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };
    private const int ReadBufferSize = 65_536;

    private bool _initialized;

    // ── Construction ──────────────────────────────────────────────────────────

    public CacheManager(string cacheRoot, ILogger<CacheManager> logger)
    {
        _cacheRoot = cacheRoot;
        _logger    = logger;
    }

    // ── ICacheManager ─────────────────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _initialized = true;

        if (!Directory.Exists(_cacheRoot)) return;

        // 1. Remove orphaned .tmp files left by a crash or hard restart
        var tmpFiles = Directory.GetFiles(_cacheRoot, "*.tmp", SearchOption.AllDirectories);
        foreach (var tmp in tmpFiles)
        {
            try { File.Delete(tmp); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete orphaned temp file: {Path}", tmp);
            }
        }
        if (tmpFiles.Length > 0)
            _logger.LogInformation("CacheManager: deleted {N} orphaned .tmp files", tmpFiles.Length);

        // 2. Load and validate every on-disk manifest
        var manifestFiles = Directory.GetFiles(_cacheRoot, "manifest.json", SearchOption.AllDirectories);
        foreach (var mf in manifestFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadManifestFromDiskAsync(mf, ct);
                if (manifest == null) continue;

                var dir = GetItemDir(manifest.ConnectorId, manifest.ItemId);
                var dirty = false;

                // Remove phantom segments (file missing on disk)
                var phantoms = manifest.Segments
                    .Where(s => !File.Exists(Path.Combine(dir, s.FileName)))
                    .ToList();
                if (phantoms.Count > 0)
                {
                    foreach (var s in phantoms) manifest.Segments.Remove(s);
                    _logger.LogWarning(
                        "CacheManager: removed {N} phantom segments from manifest {ItemId}",
                        phantoms.Count, manifest.ItemId);
                    dirty = true;
                }

                // Remove overlapping segments (legacy manifests or concurrent-write artefacts)
                dirty |= RemoveOverlappingSegments(manifest, dir);

                // Remove unaligned segments (artefacts written before the chunk-alignment fix)
                dirty |= RemoveUnalignedSegments(manifest, dir);

                // Delete .bin files on disk that are not referenced by any segment (orphans)
                var referenced = manifest.Segments.Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var binFile in Directory.GetFiles(dir, "*.bin"))
                {
                    if (!referenced.Contains(Path.GetFileName(binFile)))
                    {
                        try { File.Delete(binFile); _logger.LogInformation("CacheManager: deleted orphan {Path}", binFile); }
                        catch (Exception ex) { _logger.LogWarning(ex, "CacheManager: could not delete orphan {Path}", binFile); }
                    }
                }

                // Refresh chunk coverage metadata and always flush to apply field-name migrations
                manifest.RefreshChunkCoverage();
                await FlushManifestAsync(manifest.ConnectorId, manifest.ItemId, manifest, ct);

                _manifests[ManifestKey(manifest.ConnectorId, manifest.ItemId)] = manifest;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CacheManager: could not load manifest {Path}", mf);
            }
        }

        _logger.LogInformation("CacheManager: initialized ({N} items)", _manifests.Count);
    }

    public async Task<ChunkManifest> EnsureManifestAsync(
        string connectorId, string itemId,
        long totalSize, string contentType, string sourceUrl,
        int chunkSizeBytes = 2 * 1024 * 1024,
        CancellationToken ct = default)
    {
        var key = ManifestKey(connectorId, itemId);

        if (_manifests.TryGetValue(key, out var existing))
        {
            var dirty = false;
            if (existing.TotalSize <= 0 && totalSize > 0) { existing.TotalSize = totalSize; dirty = true; }

            if (chunkSizeBytes > 0 && existing.ChunkSize != chunkSizeBytes)
            {
                if (existing.Segments.Count == 0)
                {
                    existing.ChunkSize = chunkSizeBytes;
                    dirty = true;
                }
                else
                {
                    // Chunk size changed while segments exist → resize all segments
                    // under the manifest lock so no concurrent flush interferes.
                    var manifestLock = GetManifestLock(connectorId, itemId);
                    await manifestLock.WaitAsync(ct);
                    try
                    {
                        _logger.LogInformation(
                            "CacheManager: chunk size changed {Old}→{New} for {ItemId} ({N} segments) — resizing",
                            existing.ChunkSize, chunkSizeBytes, itemId, existing.Segments.Count);
                        var dir = GetItemDir(connectorId, itemId);
                        await ResizeSegmentsToChunkBoundariesAsync(existing, chunkSizeBytes, dir, ct);
                        await FlushManifestAsync(connectorId, itemId, existing, ct);
                    }
                    finally { manifestLock.Release(); }
                }
            }

            if (dirty) await FlushManifestAsync(connectorId, itemId, existing, ct);
            return existing;
        }

        // Try to load from disk first
        var manifestPath = GetManifestPath(connectorId, itemId);
        if (File.Exists(manifestPath))
        {
            var loaded = await ReadManifestFromDiskAsync(manifestPath, ct);
            if (loaded != null)
            {
                _manifests[key] = loaded;
                return loaded;
            }
        }

        // Create new manifest
        Directory.CreateDirectory(GetItemDir(connectorId, itemId));
        var manifest = new ChunkManifest
        {
            ConnectorId = connectorId,
            ItemId      = itemId,
            TotalSize   = totalSize,
            ChunkSize   = chunkSizeBytes > 0 ? chunkSizeBytes : 2 * 1024 * 1024,
            ContentType = contentType,
            SourceUrl   = sourceUrl,
        };

        _manifests[key] = manifest;
        await FlushManifestAsync(connectorId, itemId, manifest, ct);
        return manifest;
    }

    public async Task<ChunkManifest?> GetManifestAsync(string connectorId, string itemId)
    {
        var key = ManifestKey(connectorId, itemId);
        if (_manifests.TryGetValue(key, out var cached)) return cached;

        var path = GetManifestPath(connectorId, itemId);
        if (!File.Exists(path)) return null;

        var loaded = await ReadManifestFromDiskAsync(path, default);
        if (loaded != null) _manifests[key] = loaded;
        return loaded;
    }

    public bool IsRangeCached(ChunkManifest? manifest, long rangeStart, long rangeEnd)
        => manifest != null && manifest.TotalSize > 0 && manifest.IsRangeCached(rangeStart, rangeEnd);

    public async Task ServeCachedRangeAsync(
        ChunkManifest manifest, string connectorId, string itemId,
        long rangeStart, long rangeEnd, Stream destination, CancellationToken ct)
    {
        manifest.LastAccessAt = DateTime.UtcNow;

        // Find the single segment that fully covers [rangeStart, rangeEnd]
        var seg = manifest.Segments.FirstOrDefault(s => s.Start <= rangeStart && s.End > rangeEnd);
        if (seg == null)
            throw new InvalidOperationException(
                $"No segment covers range [{rangeStart}, {rangeEnd}] for item {manifest.ItemId}");

        var filePath   = Path.Combine(GetItemDir(connectorId, itemId), seg.FileName);
        var fileOffset = rangeStart - seg.Start;
        var bytesToServe = rangeEnd - rangeStart + 1;

        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            await using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fileOffset > 0) fs.Seek(fileOffset, SeekOrigin.Begin);

            var remaining = bytesToServe;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(remaining, buffer.Length);
                var read   = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CopyWithCacheAsync(
        Stream source, Stream destination,
        ChunkManifest manifest, string connectorId, string itemId,
        long startOffset, int flushIntervalBytes = 2 * 1024 * 1024,
        CancellationToken ct = default)
    {
        var dir = GetItemDir(connectorId, itemId);
        Directory.CreateDirectory(dir);

        if (flushIntervalBytes <= 0) flushIntervalBytes = 2 * 1024 * 1024;

        // First chunk-aligned byte offset at or after startOffset.
        // Bytes before this boundary are forwarded to the client but NOT cached
        // so that every segment starts exactly on a chunk boundary.
        var cacheFrom = startOffset % flushIntervalBytes == 0
            ? startOffset
            : (startOffset / flushIntervalBytes + 1) * flushIntervalBytes;

        var currentOffset = startOffset;
        var segStart      = cacheFrom;
        var segBytes      = 0L;
        var tmpName       = string.Empty;
        var tmpPath       = string.Empty;
        FileStream? tmpStream = null;
        string pendingKey = string.Empty;

        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    // Always forward everything to the client immediately
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);

                    var bufPos = 0;
                    while (bufPos < read)
                    {
                        var globalPos = currentOffset + bufPos;

                        // Skip phase: bytes before the first chunk-aligned boundary
                        if (globalPos < cacheFrom)
                        {
                            bufPos += (int)Math.Min(read - bufPos, cacheFrom - globalPos);
                            continue;
                        }

                        // Open a new temp file at the first aligned position
                        if (tmpStream == null)
                        {
                            tmpName   = MakeTmpName(segStart);
                            tmpPath   = Path.Combine(dir, tmpName);
                            tmpStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);

                            // Register a pending entry so other clients can wait instead of fetching source
                            pendingKey = PendingKey(connectorId, itemId, segStart);
                            _pendingSegments.TryAdd(pendingKey,
                                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                        }

                        var spaceLeft = flushIntervalBytes - (int)segBytes;
                        var toCopy    = (int)Math.Min(read - bufPos, spaceLeft);

                        await tmpStream.WriteAsync(buffer.AsMemory(bufPos, toCopy), ct);
                        segBytes += toCopy;
                        bufPos   += toCopy;

                        if (segBytes >= flushIntervalBytes)
                        {
                            // Exact flush at chunk boundary
                            await tmpStream.FlushAsync(ct);
                            await tmpStream.DisposeAsync();
                            tmpStream = null;

                            await CommitSegmentAsync(manifest, connectorId, itemId, dir, segStart, segBytes, tmpPath, CancellationToken.None);

                            // Signal waiters: chunk is committed
                            if (_pendingSegments.TryRemove(pendingKey, out var flushTcs))
                                flushTcs.TrySetResult(true);
                            pendingKey = string.Empty;

                            segStart += flushIntervalBytes;
                            segBytes  = 0;
                        }
                    }

                    currentOffset += read;
                }

                // Flush the remaining tail (< flushIntervalBytes).
                // Only commit if this is the actual end of the file — a partial segment
                // that stops mid-stream is useless and would pollute the manifest.
                if (segBytes > 0 && tmpStream != null)
                {
                    await tmpStream.FlushAsync(ct);
                    await tmpStream.DisposeAsync();
                    tmpStream = null;

                    var isFileTail = manifest.TotalSize <= 0
                        || segStart + segBytes >= manifest.TotalSize;

                    if (isFileTail)
                    {
                        await CommitSegmentAsync(manifest, connectorId, itemId, dir, segStart, segBytes, tmpPath, CancellationToken.None);
                        if (_pendingSegments.TryRemove(pendingKey, out var tailTcs))
                            tailTcs.TrySetResult(true);
                        pendingKey = string.Empty;
                    }
                    else
                    {
                        _logger.LogDebug(
                            "CacheManager: discarding partial tail [{Start},{End}) for {ItemId} — not file end",
                            segStart, segStart + segBytes, itemId);
                        if (_pendingSegments.TryRemove(pendingKey, out var discardTcs))
                            discardTcs.TrySetResult(false);
                        pendingKey = string.Empty;
                        try { File.Delete(tmpPath); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                // On client disconnect: apply 50% rule — finish the current chunk if
                // more than half is done OR someone is waiting for it.
                var isClientDisconnect = ex is IOException || ex is OperationCanceledException;
                if (isClientDisconnect && tmpStream != null && segBytes > 0 && !string.IsNullOrEmpty(pendingKey))
                {
                    var hasWaiter = _pendingWaiters.TryGetValue(pendingKey, out var wn) && wn > 0;
                    var shouldFinish = segBytes > flushIntervalBytes / 2 || hasWaiter;

                    if (shouldFinish)
                    {
                        try
                        {
                            // Continue reading from source (ignore client ct) until chunk boundary
                            int r;
                            while (segBytes < flushIntervalBytes &&
                                   (r = await source.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
                            {
                                var space = flushIntervalBytes - (int)segBytes;
                                var toCopy = Math.Min(r, space);
                                await tmpStream.WriteAsync(buffer.AsMemory(0, toCopy), CancellationToken.None);
                                segBytes += toCopy;
                            }

                            if (segBytes >= flushIntervalBytes)
                            {
                                await tmpStream.FlushAsync(CancellationToken.None);
                                await tmpStream.DisposeAsync();
                                tmpStream = null;
                                await CommitSegmentAsync(manifest, connectorId, itemId, dir, segStart, segBytes, tmpPath, CancellationToken.None);
                                if (_pendingSegments.TryRemove(pendingKey, out var finishTcs))
                                    finishTcs.TrySetResult(true);
                                pendingKey = string.Empty;
                            }
                        }
                        catch { /* best effort — fall through to cleanup below */ }
                    }
                }

                if (tmpStream != null) { try { await tmpStream.DisposeAsync(); } catch { } tmpStream = null; }
                if (!string.IsNullOrEmpty(tmpPath)) { try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { } }
                throw;
            }
            finally
            {
                if (tmpStream != null) { try { await tmpStream.DisposeAsync(); } catch { } }
                // Safety net: always signal pending (in case of unhandled path)
                if (!string.IsNullOrEmpty(pendingKey) && _pendingSegments.TryRemove(pendingKey, out var safeTcs))
                    safeTcs.TrySetResult(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Atomically renames a .tmp file to .bin, then merges it into the manifest
    /// under the per-item lock. Discards the file if the range is already covered.
    /// </summary>
    private async Task CommitSegmentAsync(
        ChunkManifest manifest, string connectorId, string itemId,
        string dir, long segStart, long segBytes, string tmpPath, CancellationToken ct)
    {
        var finalName = MakeBinName(segStart);
        var finalPath = Path.Combine(dir, finalName);

        try { File.Move(tmpPath, finalPath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CacheManager: rename failed for {ItemId} seg [{Start}]", itemId, segStart);
            try { File.Delete(tmpPath); } catch { }
            return;
        }

        // Everything after the rename uses CancellationToken.None:
        // the .bin file is committed on disk and the manifest MUST reflect it,
        // even if the HTTP request was cancelled by a client disconnect.
        var newSeg = new CachedSegment { Start = segStart, Length = segBytes, FileName = finalName };
        var manifestLock = GetManifestLock(connectorId, itemId);
        await manifestLock.WaitAsync(CancellationToken.None);
        try
        {
            if (manifest.IsRangeCached(segStart, segStart + segBytes - 1))
            {
                try { File.Delete(finalPath); } catch { }
                return;
            }

            if (manifest.StartsInsideExistingSegment(segStart))
            {
                _logger.LogDebug(
                    "CacheManager: discarding overlapping segment [{Start}, {End}) for {ItemId}",
                    segStart, segStart + segBytes, itemId);
                try { File.Delete(finalPath); } catch { }
                return;
            }

            await MergeAndCommitSegmentAsync(manifest, newSeg, dir, CancellationToken.None);
            manifest.RefreshChunkCoverage();
            manifest.LastAccessAt = DateTime.UtcNow;
            await FlushManifestAsync(connectorId, itemId, manifest, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CacheManager: merge failed for {ItemId} seg [{Start}]", itemId, segStart);
            try { File.Delete(finalPath); } catch { }
        }
        finally
        {
            manifestLock.Release();
        }
    }

    public async Task ValidateItemAsync(string connectorId, string itemId, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(connectorId, itemId);
        if (manifest == null) return;

        var dir = GetItemDir(connectorId, itemId);
        if (!Directory.Exists(dir)) return;

        var manifestLock = GetManifestLock(connectorId, itemId);
        await manifestLock.WaitAsync(ct);
        try
        {
            var dirty = false;

            // 1. Phantom segments — .bin referenced in manifest but missing on disk
            var phantoms = manifest.Segments
                .Where(s => !File.Exists(Path.Combine(dir, s.FileName)))
                .ToList();
            if (phantoms.Count > 0)
            {
                foreach (var s in phantoms) manifest.Segments.Remove(s);
                _logger.LogWarning(
                    "CacheManager.Validate: removed {N} phantom segment(s) for {ItemId}", phantoms.Count, itemId);
                dirty = true;
            }

            // 2. Unaligned segments — cover no complete chunk and are not the file tail
            dirty |= RemoveUnalignedSegments(manifest, dir);

            // 3. Orphan .bin files — present on disk but not referenced by any segment
            var referenced = manifest.Segments
                .Select(s => s.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var binFile in Directory.GetFiles(dir, "*.bin"))
            {
                if (!referenced.Contains(Path.GetFileName(binFile)))
                {
                    try
                    {
                        File.Delete(binFile);
                        _logger.LogInformation("CacheManager.Validate: deleted orphan {File}", Path.GetFileName(binFile));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CacheManager.Validate: could not delete orphan {File}", binFile);
                    }
                }
            }

            if (dirty)
            {
                manifest.RefreshChunkCoverage();
                await FlushManifestAsync(connectorId, itemId, manifest, ct);
            }
        }
        finally
        {
            manifestLock.Release();
        }
    }

    public async Task<bool> WaitForPendingSegmentAsync(
        string connectorId, string itemId, long segStart, CancellationToken ct = default)
    {
        var key = PendingKey(connectorId, itemId, segStart);
        if (!_pendingSegments.TryGetValue(key, out var tcs)) return false;

        _pendingWaiters.AddOrUpdate(key, 1, (_, v) => v + 1);
        try
        {
            // Wait without cancelling the shared TCS (other waiters must not be affected)
            if (ct.CanBeCanceled)
            {
                var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var reg = ct.Register(() => cancelTcs.TrySetCanceled(ct));
                var winner = await Task.WhenAny(tcs.Task, cancelTcs.Task).ConfigureAwait(false);
                if (winner != tcs.Task) return false;
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        catch { return false; }
        finally
        {
            _pendingWaiters.AddOrUpdate(key, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    public Task InvalidateAsync(string connectorId, string itemId)
    {
        var key = ManifestKey(connectorId, itemId);
        _manifests.TryRemove(key, out _);

        var dir = GetItemDir(connectorId, itemId);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CacheManager: could not delete cache dir {Dir}", dir);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<string> PromoteToFileAsync(
        string connectorId, string itemId, string destPath, CancellationToken ct)
    {
        var manifest = await GetManifestAsync(connectorId, itemId)
            ?? throw new InvalidOperationException($"No manifest for {connectorId}/{itemId}");

        if (!manifest.IsComplete)
            throw new InvalidOperationException($"Cache incomplete for {itemId}");

        var srcPath = Path.Combine(GetItemDir(connectorId, itemId), manifest.Segments[0].FileName);
        File.Copy(srcPath, destPath, overwrite: true);

        _logger.LogInformation("CacheManager: promoted {ItemId} → {DestPath}", itemId, destPath);
        return destPath;
    }

    public CacheStats GetStats()
    {
        var items    = _manifests.Values.ToList();
        var complete = items.Count(m => m.IsComplete);
        var total    = items.Sum(m => m.Segments.Sum(s => s.Length));
        return new CacheStats
        {
            TotalItems     = items.Count,
            CompleteItems  = complete,
            TotalSizeBytes = total,
        };
    }

    // ── Segment helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// A legitimate file tail starts exactly on a chunk boundary and extends to TotalSize.
    /// Such a segment may cover only a partial last chunk (smaller than ChunkSize) and is valid.
    /// Anything else with FirstCoveredChunk == -1 is a parasitic partial segment.
    /// </summary>
    private static bool IsLegitimateFileTail(CachedSegment seg, ChunkManifest manifest)
        => manifest.TotalSize > 0
        && manifest.ChunkSize > 0
        && seg.End >= manifest.TotalSize
        && seg.Start % manifest.ChunkSize == 0;

    // ── Unaligned segment cleanup ─────────────────────────────────────────────

    /// <summary>
    /// Two-pass cleanup for segments that are not the file tail:
    /// <list type="bullet">
    ///   <item>Pass 1 — remove segments that cover no complete chunk at all
    ///         (FirstCoveredChunk == -1). These are full artefacts from before the
    ///         chunk-alignment fix.</item>
    ///   <item>Pass 2 — truncate the file of any segment whose end extends beyond
    ///         the last complete chunk boundary. The excess bytes are partial chunk
    ///         data that can never be served and prevent adjacent-segment merging.</item>
    /// </list>
    /// Returns true if any change was made (segment removed or file truncated).
    /// </summary>
    private bool RemoveUnalignedSegments(ChunkManifest manifest, string dir)
    {
        if (manifest.TotalSize <= 0 || manifest.ChunkSize <= 0) return false;

        manifest.RefreshChunkCoverage();

        var dirty = false;

        // Pass 1: remove fully unaligned segments (cover zero complete chunks).
        // Exception: a legitimate file tail starts exactly on a chunk boundary and reaches TotalSize.
        // Anything else with FirstCoveredChunk == -1 is a partial/parasitic segment.
        var toRemove = manifest.Segments
            .Where(s => s.FirstCoveredChunk == -1 && !IsLegitimateFileTail(s, manifest))
            .ToList();
        foreach (var s in toRemove)
        {
            _logger.LogInformation(
                "CacheManager: removing unaligned segment [{Start},{End}) (no complete chunk) for {ItemId}",
                s.Start, s.End, manifest.ItemId);
            manifest.Segments.Remove(s);
            try { File.Delete(Path.Combine(dir, s.FileName)); } catch { }
            dirty = true;
        }

        // Pass 2: truncate any partial-chunk tail on segments that are not the file tail.
        // The manifest Length and the physical file are both set to the chunk-aligned size
        // so that a future chunk-size change always starts from a clean boundary.
        foreach (var seg in manifest.Segments.ToList())
        {
            if (IsLegitimateFileTail(seg, manifest)) continue;   // legitimate file tail — keep as-is
            if (seg.LastCoveredChunk < 0)   continue;           // no complete chunk (handled above)

            var alignedEnd   = (long)(seg.LastCoveredChunk + 1) * manifest.ChunkSize;
            var usableLength = alignedEnd - seg.Start;
            var filePath     = Path.Combine(dir, seg.FileName);

            // Sanity: file must be at least as large as the usable length
            long fileSize;
            try   { fileSize = new FileInfo(filePath).Length; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CacheManager: could not stat [{Start},{End}) for {ItemId} — removing",
                    seg.Start, seg.End, manifest.ItemId);
                manifest.Segments.Remove(seg);
                dirty = true;
                continue;
            }

            if (fileSize < usableLength)
            {
                _logger.LogWarning(
                    "CacheManager: file too small ({FileSize} < {Usable}) for segment [{Start},{End}) {ItemId} — removing",
                    fileSize, usableLength, seg.Start, seg.End, manifest.ItemId);
                manifest.Segments.Remove(seg);
                try { File.Delete(filePath); } catch { }
                dirty = true;
                continue;
            }

            if (seg.Length == usableLength) continue;   // already aligned — nothing to do

            // Truncate file to aligned size, then update manifest
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                fs.SetLength(usableLength);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CacheManager: could not truncate [{Start},{End}) for {ItemId} — skipping",
                    seg.Start, seg.End, manifest.ItemId);
                continue;
            }

            _logger.LogInformation(
                "CacheManager: truncated tail [{Start},{OldEnd})→[{Start},{NewEnd}) for {ItemId}",
                seg.Start, seg.End, seg.Start + usableLength, manifest.ItemId);
            seg.Length = usableLength;
            dirty = true;
        }

        return dirty;
    }

    // ── Overlap normalization ─────────────────────────────────────────────────

    /// <summary>
    /// Removes any segment whose start byte falls inside another segment (legacy overlaps).
    /// The overlapping file is deleted from disk.
    /// Returns true if any segment was removed.
    /// </summary>
    private bool RemoveOverlappingSegments(ChunkManifest manifest, string dir)
    {
        if (manifest.Segments.Count <= 1) return false;

        manifest.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));

        var toRemove = new List<CachedSegment>();
        for (int i = 0; i < manifest.Segments.Count; i++)
        {
            var outer = manifest.Segments[i];
            for (int j = 0; j < manifest.Segments.Count; j++)
            {
                if (i == j || toRemove.Contains(manifest.Segments[j])) continue;
                var inner = manifest.Segments[j];
                // If inner.Start is strictly inside outer → it overlaps
                if (outer.Start <= inner.Start && outer.End > inner.Start && i != j)
                {
                    toRemove.Add(inner);
                    _logger.LogWarning(
                        "CacheManager: removing overlapping segment [{Start}, {End}) (inside [{OStart}, {OEnd})) for {ItemId}",
                        inner.Start, inner.End, outer.Start, outer.End, manifest.ItemId);
                    try { File.Delete(Path.Combine(dir, inner.FileName)); } catch { }
                }
            }
        }

        foreach (var s in toRemove) manifest.Segments.Remove(s);
        return toRemove.Count > 0;
    }

    // ── Chunk-size resize ─────────────────────────────────────────────────────

    /// <summary>
    /// When the configured chunk size changes, adjusts every segment so that its file
    /// contains only complete chunks at the new size:
    /// <list type="bullet">
    ///   <item>Tail bytes after the last complete chunk boundary are truncated (SetLength).</item>
    ///   <item>Leading bytes before the first complete chunk boundary are removed by
    ///         creating a new file at the aligned offset and deleting the old one.</item>
    ///   <item>Segments that contain no complete chunk (and are not the file tail) are deleted.</item>
    ///   <item>CoveredChunks fields are refreshed with the new size.</item>
    /// </list>
    /// Must be called under the per-item manifest lock.
    /// </summary>
    private async Task ResizeSegmentsToChunkBoundariesAsync(
        ChunkManifest manifest, int newChunkSize, string dir, CancellationToken ct)
    {
        var toRemove = new List<CachedSegment>();

        foreach (var seg in manifest.Segments)
        {
            ct.ThrowIfCancellationRequested();

            var isFileTail = IsLegitimateFileTail(seg, manifest);

            // Byte offset (within the file) where the new aligned content starts
            var alignedStart = (long)Math.Ceiling((double)seg.Start / newChunkSize) * newChunkSize;

            // Exclusive end of the last complete chunk that fits inside this segment
            long alignedEnd;
            if (isFileTail)
                alignedEnd = manifest.TotalSize;               // keep the file tail even if < newChunkSize
            else
                alignedEnd = (long)Math.Floor((double)seg.End / newChunkSize) * newChunkSize;

            if (alignedStart >= alignedEnd)
            {
                // No complete chunk fits and not the file tail → discard
                _logger.LogInformation(
                    "CacheManager: deleting segment [{Start},{End}) — no complete {ChunkSize}-byte chunk fits",
                    seg.Start, seg.End, newChunkSize);
                toRemove.Add(seg);
                try { File.Delete(Path.Combine(dir, seg.FileName)); } catch { }
                continue;
            }

            var newLength = alignedEnd - alignedStart;
            var skipHead  = alignedStart - seg.Start;   // bytes to drop from the front of the file
            var dropTail  = seg.End - alignedEnd;        // bytes to drop from the end

            var oldPath = Path.Combine(dir, seg.FileName);

            try
            {
                if (skipHead > 0)
                {
                    // Must create a new file starting at the aligned offset
                    var newFileName = MakeBinName(alignedStart);
                    var newPath     = Path.Combine(dir, newFileName);

                    var buf = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
                    try
                    {
                        await using (var inFs  = new FileStream(oldPath, FileMode.Open,   FileAccess.Read,  FileShare.None))
                        await using (var outFs = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            inFs.Seek(skipHead, SeekOrigin.Begin);
                            var remaining = newLength;
                            int read;
                            while (remaining > 0 &&
                                   (read = await inFs.ReadAsync(buf.AsMemory(0, (int)Math.Min(remaining, buf.Length)), ct)) > 0)
                            {
                                await outFs.WriteAsync(buf.AsMemory(0, read), ct);
                                remaining -= read;
                            }
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buf); }

                    File.Delete(oldPath);
                    seg.FileName = newFileName;
                }
                else if (dropTail > 0)
                {
                    // Simple in-place truncation from the end
                    using var fs = new FileStream(oldPath, FileMode.Open, FileAccess.Write, FileShare.None);
                    fs.SetLength(newLength);
                }

                seg.Start  = alignedStart;
                seg.Length = newLength;

                _logger.LogInformation(
                    "CacheManager: resized segment → [{Start},{End}) ({SkipHead} head / {DropTail} tail bytes removed)",
                    seg.Start, seg.End, skipHead, dropTail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CacheManager: failed to resize segment [{Start},{End}) — deleting", seg.Start, seg.End);
                toRemove.Add(seg);
                try { File.Delete(oldPath); } catch { }
            }
        }

        foreach (var s in toRemove) manifest.Segments.Remove(s);
        manifest.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));
        manifest.ChunkSize = newChunkSize;
        manifest.RefreshChunkCoverage();
    }

    // ── Segment merge ─────────────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="newSeg"/> to the manifest, merging it with any adjacent
    /// segment on the left and/or right by physically concatenating the .bin files.
    /// Must be called under the per-item manifest lock.
    /// </summary>
    private async Task MergeAndCommitSegmentAsync(
        ChunkManifest manifest, CachedSegment newSeg, string dir, CancellationToken ct)
    {
        var mergedStart    = newSeg.Start;
        var mergedLength   = newSeg.Length;
        var mergedFileName = newSeg.FileName;
        var mergedFilePath = Path.Combine(dir, mergedFileName);

        // ── Left neighbour: a segment whose exclusive end equals our start ──
        var left = manifest.Segments.FirstOrDefault(s => s.End == newSeg.Start);
        if (left != null)
        {
            var leftPath = Path.Combine(dir, left.FileName);
            try
            {
                await using (var leftFs = new FileStream(
                    leftPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                await using (var newFs = new FileStream(
                    mergedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    await newFs.CopyToAsync(leftFs, ct);

                File.Delete(mergedFilePath);
                manifest.Segments.Remove(left);

                mergedStart    = left.Start;
                mergedLength   = left.Length + newSeg.Length;
                mergedFileName = left.FileName;
                mergedFilePath = leftPath;
            }
            catch (Exception ex)
            {
                // Non-fatal: keep newSeg without merging into left
                _logger.LogWarning(ex, "CacheManager: left-merge failed for {ItemId}", manifest.ItemId);
            }
        }

        // ── Right neighbour: a segment whose start equals our exclusive end ──
        var right = manifest.Segments.FirstOrDefault(s => s.Start == mergedStart + mergedLength);
        if (right != null)
        {
            var rightPath = Path.Combine(dir, right.FileName);
            try
            {
                await using (var mergedFs = new FileStream(
                    mergedFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                await using (var rightFs = new FileStream(
                    rightPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    await rightFs.CopyToAsync(mergedFs, ct);

                File.Delete(rightPath);
                manifest.Segments.Remove(right);
                mergedLength += right.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CacheManager: right-merge failed for {ItemId}", manifest.ItemId);
            }
        }

        manifest.Segments.Add(new CachedSegment
        {
            Start    = mergedStart,
            Length   = mergedLength,
            FileName = mergedFileName,
        });
        manifest.Segments.Sort((a, b) => a.Start.CompareTo(b.Start));

        _logger.LogDebug(
            "CacheManager: cached [{Start}, {End}) for {ItemId} → {N} segment(s)",
            mergedStart, mergedStart + mergedLength, manifest.ItemId, manifest.Segments.Count);
    }

    // ── Manifest persistence ──────────────────────────────────────────────────

    private async Task FlushManifestAsync(
        string connectorId, string itemId, ChunkManifest manifest, CancellationToken ct)
    {
        var dir      = GetItemDir(connectorId, itemId);
        Directory.CreateDirectory(dir);

        var tmpPath   = GetManifestPath(connectorId, itemId) + ".tmp";
        var finalPath = GetManifestPath(connectorId, itemId);

        var json = JsonSerializer.Serialize(manifest, _jsonOpts);
        await File.WriteAllTextAsync(tmpPath, json, ct);
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    private static async Task<ChunkManifest?> ReadManifestFromDiskAsync(
        string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<ChunkManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    // ── Lock factory ──────────────────────────────────────────────────────────

    private SemaphoreSlim GetManifestLock(string connectorId, string itemId)
        => _manifestLocks.GetOrAdd(ManifestKey(connectorId, itemId), _ => new SemaphoreSlim(1, 1));

    // ── Segment name helpers ──────────────────────────────────────────────────

    private static string PendingKey(string connectorId, string itemId, long segStart)
        => $"{connectorId}:{itemId}:{segStart}";

    private static string MakeTmpName(long segStart)
        => $"seg_{segStart:D20}_{Guid.NewGuid():N}.tmp";

    private static string MakeBinName(long segStart)
        => $"seg_{segStart:D20}_{Guid.NewGuid():N}.bin";

    // ── Path helpers ──────────────────────────────────────────────────────────

    private string GetItemDir(string connectorId, string itemId)
        => Path.Combine(_cacheRoot, Sanitize(connectorId), Sanitize(itemId));

    private string GetManifestPath(string connectorId, string itemId)
        => Path.Combine(GetItemDir(connectorId, itemId), "manifest.json");

    private static string ManifestKey(string connectorId, string itemId)
        => $"{connectorId}:{itemId}";

    private static string Sanitize(string s)
        => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
