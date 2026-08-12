using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Services;

// Resolves PendingParts of a lazy multipart RAR archive on demand.
// First reader to need part N pays the cost (~1 segment fetch + parse);
// subsequent readers reuse the resolved FilePart. The whole resolved
// archive is written back to the blob-store so restarts also reuse it.
public class LazyRarResolver
{
    private readonly Func<DavMultipartFile, DavMultipartFile.PendingPart, CancellationToken,
        Task<DavMultipartFile.FilePart>> _resolvePart;
    private readonly Func<int> _getMaxConcurrency;
    private readonly ConcurrentDictionary<Guid, DavMultipartFile> _activeFiles = new();

    public LazyRarResolver(UsenetStreamingClient usenetClient, ConfigManager configManager)
    {
        _resolvePart = (mpf, pending, ct) => DoResolveAsync(usenetClient, mpf, pending, ct);
        _getMaxConcurrency = configManager.GetMaxDownloadConnections;
    }

    internal LazyRarResolver(
        Func<DavMultipartFile, DavMultipartFile.PendingPart, CancellationToken,
            Task<DavMultipartFile.FilePart>> resolvePart,
        int maxConcurrency = 8)
    {
        _resolvePart = resolvePart;
        _getMaxConcurrency = () => maxConcurrency;
    }

    // Coalesces concurrent resolution requests for the same volume.
    // Keyed by the volume's first segment ID so two readers asking for the
    // same trailing part share one parse, even if they hit different
    // FileParts.Length snapshots (which the old (Guid,int) key broke).
    private readonly ConcurrentDictionary<(Guid, string), Task<DavMultipartFile.FilePart>> _inFlight = new();

    private readonly ConcurrentDictionary<Guid, Persistor> _persistors = new();

    private sealed class Persistor
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public long LatestStamp;
    }

    // Resolve enough volumes forward from the known prefix to cover
    // targetByteOffset. Bulk callers can still ask for long.MaxValue to map
    // the whole archive; range reads use EnsureResolvedForReadAsync so a seek
    // near EOF can take the shorter path from the tail.
    public async Task<DavMultipartFile.Meta> EnsureResolvedThroughAsync(
        DavMultipartFile mpf,
        long targetByteOffset,
        CancellationToken ct)
    {
        mpf = GetActiveFile(mpf);
        var meta = mpf.Metadata;
        if (!meta.IsLazy) return meta;

        while (true)
        {
            // Old MemoryPack blobs may deserialize PendingParts as null despite
            // the property initializer; treat that as "nothing to resolve".
            var pending = meta.PendingParts ?? [];
            if (pending.Length == 0) return meta;

            var resolvedBytes = SumResolvedBytes(meta);
            if (resolvedBytes > targetByteOffset) return meta;

            // Decide how many trailing parts to resolve based on estimates. The
            // estimates are adjusted at import time so cumulative sums match the
            // true file length. Real RAR continuation headers can still differ
            // from the import-time estimate, so after committing exact ranges we
            // loop until the exact resolved map really covers targetByteOffset.
            var count = 0;
            var runningTotal = resolvedBytes;
            foreach (var p in pending)
            {
                count++;
                runningTotal += p.EstimatedDataSize;
                if (runningTotal > targetByteOffset) break;
            }

            var partsToResolve = new DavMultipartFile.PendingPart[count];
            Array.Copy(pending, partsToResolve, count);

            // Run resolutions in parallel, bounded by the provider plan limit.
            // Use the same cap that governs the rest of the queue processor so
            // we never burst past what the user's provider plan allows.
            var maxConcurrency = Math.Max(1, _getMaxConcurrency());
            using var semaphore = new SemaphoreSlim(maxConcurrency);

            var resolveTasks = partsToResolve.Select(async part =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await GetOrStartResolutionAsync(mpf, part, ct).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            var beforeResolvedBytes = resolvedBytes;
            var beforePendingCount = pending.Length;
            var resolveds = await Task.WhenAll(resolveTasks).ConfigureAwait(false);
            meta = CommitResolvedBatch(mpf, resolveds);

            var afterPendingCount = (meta.PendingParts ?? []).Length;
            if (SumResolvedBytes(meta) == beforeResolvedBytes && afterPendingCount == beforePendingCount)
            {
                Log.Warning(
                    "Lazy RAR resolver made no progress for {Id} while seeking to {Offset}; archive mapping may be invalid.",
                    mpf.Id, targetByteOffset);
                return meta;
            }
        }
    }

    // Resolve the archive mapping required for one read. Direction selection,
    // coalescing, and persistence stay behind this interface: callers ask for
    // a byte, not for a particular RAR volume. The resolver compares how many
    // unresolved parts are required from either end and chooses the shorter
    // exact path.
    public async Task<DavMultipartFile.Meta> EnsureResolvedForReadAsync(
        DavMultipartFile mpf,
        long targetByteOffset,
        long totalLength,
        CancellationToken ct)
    {
        mpf = GetActiveFile(mpf);
        while (true)
        {
            var meta = mpf.Metadata;
            if (!meta.IsLazy || IsOffsetResolved(meta, targetByteOffset, totalLength)) return meta;

            var pending = meta.PendingParts ?? [];
            if (pending.Length == 0) return meta;

            var prefixCount = CountPrefixPartsRequired(meta, pending, targetByteOffset);
            var tailCount = CountTailPartsRequired(meta, pending, targetByteOffset, totalLength);
            if (tailCount >= prefixCount)
                return await EnsureResolvedThroughAsync(mpf, targetByteOffset, ct).ConfigureAwait(false);

            var beforePendingCount = pending.Length;
            var startedAt = Environment.TickCount64;
            Log.Debug(
                "Lazy RAR read-map id={Id} direction=tail plannedParts={Parts} pending={Pending} bytesFromEnd={BytesFromEnd}",
                mpf.Id, tailCount, beforePendingCount, totalLength - targetByteOffset);
            meta = await ResolveTailAsync(mpf, pending, tailCount, ct).ConfigureAwait(false);
            Log.Debug(
                "Lazy RAR read-map id={Id} direction=tail resolvedParts={Parts} pending={Pending} ms={ElapsedMs}",
                mpf.Id,
                beforePendingCount - (meta.PendingParts ?? []).Length,
                (meta.PendingParts ?? []).Length,
                Environment.TickCount64 - startedAt);
            if ((meta.PendingParts ?? []).Length == beforePendingCount)
            {
                Log.Warning(
                    "Lazy RAR tail resolver made no progress for {Id} while seeking to {Offset}; archive mapping may be invalid.",
                    mpf.Id, targetByteOffset);
                return meta;
            }
        }
    }

    // Convenience for the sequential read path (DavMultipartFileStream
    // crossing a single volume boundary during playback). Resolves just one
    // part — enough to keep the iterator advancing.
    public async Task<DavMultipartFile.Meta> ResolveNextAsync(
        DavMultipartFile mpf,
        CancellationToken ct)
    {
        mpf = GetActiveFile(mpf);
        var meta = mpf.Metadata;
        var pending = meta.PendingParts ?? [];
        if (!meta.IsLazy || pending.Length == 0) return meta;

        var resolved = await GetOrStartResolutionAsync(mpf, pending[0], ct).ConfigureAwait(false);
        return CommitResolvedBatch(mpf, [resolved]);
    }

    // WebDAV deserializes a fresh DavMultipartFile for each request. Keep one
    // canonical mutable state per active archive so a foreground tail commit
    // and a background prefix commit merge instead of racing to overwrite the
    // same blob with divergent snapshots.
    private DavMultipartFile GetActiveFile(DavMultipartFile candidate) =>
        _activeFiles.GetOrAdd(candidate.Id, candidate);

    private async Task<DavMultipartFile.Meta> ResolveTailAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart[] pending,
        int count,
        CancellationToken ct)
    {
        count = Math.Clamp(count, 1, pending.Length);
        var partsToResolve = new DavMultipartFile.PendingPart[count];
        Array.Copy(pending, pending.Length - count, partsToResolve, 0, count);

        var maxConcurrency = Math.Max(1, _getMaxConcurrency());
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var resolveTasks = partsToResolve.Select(async part =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await GetOrStartResolutionAsync(mpf, part, ct).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        var resolveds = await Task.WhenAll(resolveTasks).ConfigureAwait(false);
        return CommitResolvedTailBatch(mpf, resolveds);
    }

    private static bool IsOffsetResolved(
        DavMultipartFile.Meta meta,
        long byteOffset,
        long totalLength)
    {
        if (SumResolvedBytes(meta) > byteOffset) return true;
        var tailBytes = SumTailBytes(meta);
        return tailBytes > 0 && byteOffset >= totalLength - tailBytes;
    }

    private static int CountPrefixPartsRequired(
        DavMultipartFile.Meta meta,
        DavMultipartFile.PendingPart[] pending,
        long byteOffset)
    {
        var runningTotal = SumResolvedBytes(meta);
        for (var i = 0; i < pending.Length; i++)
        {
            runningTotal += pending[i].EstimatedDataSize;
            if (runningTotal > byteOffset) return i + 1;
        }
        return pending.Length;
    }

    private static int CountTailPartsRequired(
        DavMultipartFile.Meta meta,
        DavMultipartFile.PendingPart[] pending,
        long byteOffset,
        long totalLength)
    {
        var bytesRequiredFromEnd = totalLength - byteOffset;
        var runningTotal = SumTailBytes(meta);
        for (var i = pending.Length - 1; i >= 0; i--)
        {
            runningTotal += pending[i].EstimatedDataSize;
            if (runningTotal >= bytesRequiredFromEnd) return pending.Length - i;
        }
        return pending.Length;
    }

    // Coalesce by the part's first segment ID. Two concurrent readers
    // asking for the same volume share one resolution regardless of where
    // it currently sits in PendingParts.
    private Task<DavMultipartFile.FilePart> GetOrStartResolutionAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        CancellationToken callerCt)
    {
        var firstSeg = pending.SegmentIds.Length > 0 ? pending.SegmentIds[0] : "";
        var key = (mpf.Id, firstSeg);

        // Shared work has its own non-cancelling token so one caller bailing
        // out does not kill resolution for other readers. Preserve the first
        // caller's download priority on that token: an on-demand range read is
        // playback work, whereas background pre-warming remains low priority.
        var shared = _inFlight.GetOrAdd(key, k =>
        {
            var task = ResolveSharedAsync(mpf, pending, callerCt);
            // Drop the entry once done so the dictionary doesn't grow
            // unbounded; the result lives in FileParts after commit.
            _ = task.ContinueWith(t => _inFlight.TryRemove(k, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        });

        return shared.WaitAsync(callerCt);
    }

    private async Task<DavMultipartFile.FilePart> ResolveSharedAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        CancellationToken callerCt)
    {
        using var sharedCts = new CancellationTokenSource();
        using var priorityScope = sharedCts.Token.SetContext(
            callerCt.GetContext<DownloadPriorityContext>());
        return await _resolvePart(mpf, pending, sharedCts.Token).ConfigureAwait(false);
    }

    private static async Task<DavMultipartFile.FilePart> DoResolveAsync(
        UsenetStreamingClient usenetClient,
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        var pathInArchive = meta.PathInArchive
            ?? throw new InvalidOperationException("Lazy RAR meta missing PathInArchive.");

        await using var stream = usenetClient.GetFileStream(
            pending.SegmentIds, pending.SegmentIdByteRange.Count, readAheadBytes: 0);

        // Find-and-stop so SharpCompress never seeks past the matched header.
        // The seek would force NzbFileStream to fire InterpolationSearch
        // (~7 STAT calls), which is the main reason naïve full-walk
        // resolution stalls playback at every volume boundary.
        var match = await RarUtil.FindFirstFileHeaderAsync(
            stream,
            meta.ArchivePassword,
            h => h.GetFileName() == pathInArchive,
            ct).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Lazy RAR resolution: continuation header for '{pathInArchive}' not found in trailing volume.");

        var dataStart = match.GetDataStartPosition();
        var dataSize = match.GetAdditionalDataSize();
        return new DavMultipartFile.FilePart
        {
            SegmentIds = pending.SegmentIds,
            SegmentIdByteRange = LongRange.FromStartAndSize(0, dataStart + dataSize),
            FilePartByteRange = LongRange.FromStartAndSize(dataStart, dataSize),
        };
    }

    // Atomically appends consecutive resolveds that match the head of
    // PendingParts. Race-safe: another reader's concurrent commit may have
    // already moved some/all of our resolveds across, in which case we
    // skip them silently. Persists fire-and-forget — a failed write only
    // costs us a re-resolve after restart.
    private DavMultipartFile.Meta CommitResolvedBatch(DavMultipartFile mpf, DavMultipartFile.FilePart[] resolveds)
    {
        if (resolveds.Length == 0) return mpf.Metadata;

        lock (mpf)
        {
            var meta = mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            var pendingParts = meta.PendingParts ?? [];

            // Find where our batch lines up with the current pending head.
            // A concurrent commit may have already advanced past the leading
            // resolveds; skip them and start matching from wherever the
            // current pending[0] is in our batch.
            var startIdx = 0;
            while (startIdx < resolveds.Length)
            {
                if (pendingParts.Length > 0
                    && pendingParts[0].SegmentIds.SequenceEqual(resolveds[startIdx].SegmentIds))
                {
                    break;
                }
                startIdx++;
            }

            // Match consecutive resolveds against consecutive pending head.
            var matchedCount = 0;
            while (startIdx + matchedCount < resolveds.Length
                   && matchedCount < pendingParts.Length
                   && pendingParts[matchedCount].SegmentIds
                       .SequenceEqual(resolveds[startIdx + matchedCount].SegmentIds))
            {
                matchedCount++;
            }

            if (matchedCount == 0) return meta;

            var newParts = new DavMultipartFile.FilePart[fileParts.Length + matchedCount];
            Array.Copy(fileParts, newParts, fileParts.Length);
            for (var i = 0; i < matchedCount; i++)
                newParts[fileParts.Length + i] = resolveds[startIdx + i];

            var newPending = new DavMultipartFile.PendingPart[pendingParts.Length - matchedCount];
            Array.Copy(pendingParts, matchedCount, newPending, 0, newPending.Length);

            var newMeta = CreateMetadata(
                meta,
                newParts,
                newPending,
                meta.TailFileParts ?? []);

            mpf.Metadata = newMeta;
            _ = SchedulePersistAsync(mpf);
            return newMeta;
        }
    }

    // Atomically prepends consecutive resolveds that match the tail of
    // PendingParts. The stored tail remains in forward playback order even
    // though resolution planning walks backward from EOF.
    private DavMultipartFile.Meta CommitResolvedTailBatch(
        DavMultipartFile mpf,
        DavMultipartFile.FilePart[] resolveds)
    {
        if (resolveds.Length == 0) return mpf.Metadata;

        lock (mpf)
        {
            var meta = mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            var pendingParts = meta.PendingParts ?? [];
            var tailParts = meta.TailFileParts ?? [];
            if (pendingParts.Length == 0) return meta;

            // A concurrent tail reader may already have committed some of the
            // trailing results. Find the latest result that still matches the
            // current pending tail, then walk backward while both remain
            // consecutive.
            var endIdx = resolveds.Length - 1;
            while (endIdx >= 0
                   && !pendingParts[^1].SegmentIds.SequenceEqual(resolveds[endIdx].SegmentIds))
            {
                endIdx--;
            }

            var matchedCount = 0;
            while (endIdx - matchedCount >= 0
                   && pendingParts.Length - 1 - matchedCount >= 0
                   && pendingParts[^(matchedCount + 1)].SegmentIds
                       .SequenceEqual(resolveds[endIdx - matchedCount].SegmentIds))
            {
                matchedCount++;
            }

            if (matchedCount == 0) return meta;

            var firstResolvedIdx = endIdx - matchedCount + 1;
            var newTail = new DavMultipartFile.FilePart[matchedCount + tailParts.Length];
            Array.Copy(resolveds, firstResolvedIdx, newTail, 0, matchedCount);
            Array.Copy(tailParts, 0, newTail, matchedCount, tailParts.Length);

            var newPending = new DavMultipartFile.PendingPart[pendingParts.Length - matchedCount];
            Array.Copy(pendingParts, newPending, newPending.Length);

            var newMeta = CreateMetadata(meta, fileParts, newPending, newTail);
            mpf.Metadata = newMeta;
            _ = SchedulePersistAsync(mpf);
            return newMeta;
        }
    }

    private static DavMultipartFile.Meta CreateMetadata(
        DavMultipartFile.Meta previous,
        DavMultipartFile.FilePart[] fileParts,
        DavMultipartFile.PendingPart[] pendingParts,
        DavMultipartFile.FilePart[] tailParts)
    {
        if (pendingParts.Length == 0 && tailParts.Length > 0)
        {
            var completed = new DavMultipartFile.FilePart[fileParts.Length + tailParts.Length];
            Array.Copy(fileParts, completed, fileParts.Length);
            Array.Copy(tailParts, 0, completed, fileParts.Length, tailParts.Length);
            fileParts = completed;
            tailParts = [];
        }

        return new DavMultipartFile.Meta
        {
            AesParams = previous.AesParams,
            FileParts = fileParts,
            IsLazy = pendingParts.Length > 0,
            PathInArchive = previous.PathInArchive,
            ArchivePassword = previous.ArchivePassword,
            PendingParts = pendingParts,
            TailFileParts = tailParts,
        };
    }

    private static long SumResolvedBytes(DavMultipartFile.Meta meta)
    {
        var sum = 0L;
        foreach (var p in meta.FileParts ?? []) sum += p.FilePartByteRange.Count;
        return sum;
    }

    private static long SumTailBytes(DavMultipartFile.Meta meta)
    {
        var sum = 0L;
        foreach (var p in meta.TailFileParts ?? []) sum += p.FilePartByteRange.Count;
        return sum;
    }

    private async Task SchedulePersistAsync(DavMultipartFile mpf)
    {
        var p = _persistors.GetOrAdd(mpf.Id, _ => new Persistor());
        var myStamp = Interlocked.Increment(ref p.LatestStamp);
        var removeActiveFile = false;

        await p.Sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref p.LatestStamp) != myStamp) return;
            await BlobStore.WriteBlob(mpf.Id, mpf).ConfigureAwait(false);
            removeActiveFile = !mpf.Metadata.IsLazy
                               && Volatile.Read(ref p.LatestStamp) == myStamp;
        }
        catch (Exception e)
        {
            Log.Warning(e,
                "Failed to persist lazy-resolved RAR multipart {Id}; will re-resolve on next restart",
                mpf.Id);
        }
        finally
        {
            p.Sem.Release();
            if (removeActiveFile
                && _activeFiles.TryGetValue(mpf.Id, out var active)
                && ReferenceEquals(active, mpf))
            {
                _activeFiles.TryRemove(mpf.Id, out _);
            }
        }
    }
}
