using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Services;

// Resolves PendingParts of a lazy multipart RAR archive on demand.
// First reader to need part N parses its prep-cached header prefix (or fetches
// the segment for older blobs); subsequent readers reuse the resolved
// FilePart. The whole resolved archive is written back to the blob-store so
// restarts also reuse it.
public class LazyRarResolver(UsenetStreamingClient usenetClient, ConfigManager configManager) : IDisposable
{
    private const int MaxConcurrentPasswordedHeaderParses = 1;
    private const int MaxConcurrentPasswordedNetworkHeaderParses = 2;

    private readonly LazyRarResolutionCache _resolutionCache = new();
    private readonly SemaphoreSlim _passwordedNetworkHeaderParses =
        new(MaxConcurrentPasswordedNetworkHeaderParses);

    private readonly ConcurrentDictionary<Guid, Persistor> _persistors = new();

    private sealed class Persistor
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public long LatestStamp;
    }

    // Resolve enough trailing volumes to cover targetByteOffset and return
    // the updated Meta. Needed volumes run in bounded parallel — critical for
    // the end-of-file metadata read a player issues on open, which otherwise
    // serializes one volume at a time and stalls playback for seconds.
    public async Task<DavMultipartFile.Meta> EnsureResolvedThroughAsync(
        DavMultipartFile mpf,
        long targetByteOffset,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        if (!meta.IsLazy) return meta;

        // Header-encrypted RAR5 volumes in one set share their KDF parameters.
        // Scope the derived material to this archive walk: this removes repeat
        // PBKDF2 work without retaining passwords or keys in a process cache.
        var rar5DerivedKeyCache = meta.ArchivePassword is null
            ? null
            : new Rar5DerivedKeyCache();

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

            // Preserve configured fan-out for ordinary RARs. Passworded RAR
            // headers can perform CPU-heavy key derivation for every volume,
            // so keep that path from saturating the host and provider pools.
            var maxConcurrency = GetResolutionConcurrency(
                configManager.GetMaxDownloadConnections(),
                meta.ArchivePassword);
            using var semaphore = new SemaphoreSlim(maxConcurrency);

            var resolveTasks = partsToResolve.Select(async part =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await GetOrStartResolutionAsync(
                        mpf, part, rar5DerivedKeyCache, ct).ConfigureAwait(false);
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

    internal static int GetResolutionConcurrency(
        int configuredConcurrency,
        string? archivePassword)
    {
        var concurrency = Math.Max(1, configuredConcurrency);
        return archivePassword is null
            ? concurrency
            : Math.Min(concurrency, MaxConcurrentPasswordedHeaderParses);
    }

    // Convenience for the sequential read path (DavMultipartFileStream
    // crossing a single volume boundary during playback). Resolves just one
    // part — enough to keep the iterator advancing.
    public async Task<DavMultipartFile.Meta> ResolveNextAsync(
        DavMultipartFile mpf,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        var pending = meta.PendingParts ?? [];
        if (!meta.IsLazy || pending.Length == 0) return meta;

        var rar5DerivedKeyCache = meta.ArchivePassword is null
            ? null
            : new Rar5DerivedKeyCache();
        var resolved = await GetOrStartResolutionAsync(
            mpf, pending[0], rar5DerivedKeyCache, ct).ConfigureAwait(false);
        return CommitResolvedBatch(mpf, [resolved]);
    }

    // Coalesce by the part's first segment ID. Two concurrent readers
    // asking for the same volume share one resolution regardless of where
    // it currently sits in PendingParts.
    private Task<DavMultipartFile.FilePart> GetOrStartResolutionAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        Rar5DerivedKeyCache? rar5DerivedKeyCache,
        CancellationToken callerCt)
    {
        var firstSeg = pending.SegmentIds.FirstOrDefault()
            ?? throw new InvalidDataException("Lazy RAR pending volume has no segment IDs.");

        // CancellationToken.None for the shared work so one caller bailing
        // out doesn't kill resolution for others waiting on it.
        return _resolutionCache.GetOrCreateAsync(
            mpf.Id,
            firstSeg,
            () => DoResolveAsync(
                mpf, pending, rar5DerivedKeyCache, CancellationToken.None),
            callerCt);
    }

    private async Task<DavMultipartFile.FilePart> DoResolveAsync(
        DavMultipartFile mpf,
        DavMultipartFile.PendingPart pending,
        Rar5DerivedKeyCache? rar5DerivedKeyCache,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        var pathInArchive = meta.PathInArchive
            ?? throw new InvalidOperationException("Lazy RAR meta missing PathInArchive.");

        IRarHeader? match = null;
        if (pending.HeaderPrefix is { Length: > 0 } prefix)
        {
            try
            {
                await using var prefixStream = new MemoryStream(prefix, writable: false);
                match = await FindContinuationHeaderAsync(
                    prefixStream,
                    meta.ArchivePassword,
                    pathInArchive,
                    rar5DerivedKeyCache,
                    ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                // Prefix parsing is an optimization. Unusual oversized headers
                // and old/corrupt cached prefixes remain supported by opening
                // the authoritative volume stream below.
                Log.Debug(e,
                    "Cached lazy RAR header prefix was insufficient for {Id}; retrying from Usenet.",
                    mpf.Id);
            }
        }

        if (match is null)
        {
            var gateNetworkParse = meta.ArchivePassword is not null;
            if (gateNetworkParse)
                await _passwordedNetworkHeaderParses.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var stream = usenetClient.GetFileStream(
                    pending.SegmentIds, pending.SegmentIdByteRange.Count, readAheadBytes: 0);
                match = await FindContinuationHeaderAsync(
                    stream,
                    meta.ArchivePassword,
                    pathInArchive,
                    rar5DerivedKeyCache,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                if (gateNetworkParse) _passwordedNetworkHeaderParses.Release();
            }
        }

        if (match is null)
            throw new InvalidDataException(
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

    private static Task<IRarHeader?> FindContinuationHeaderAsync(
        Stream stream,
        string? archivePassword,
        string pathInArchive,
        Rar5DerivedKeyCache? rar5DerivedKeyCache,
        CancellationToken ct) =>
        RarUtil.FindFirstFileHeaderAsync(
            stream,
            archivePassword,
            header => header.GetFileName() == pathInArchive,
            rar5DerivedKeyCache,
            ct);

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

            var newMeta = new DavMultipartFile.Meta
            {
                AesParams = meta.AesParams,
                FileParts = newParts,
                IsLazy = newPending.Length > 0,
                PathInArchive = meta.PathInArchive,
                ArchivePassword = meta.ArchivePassword,
                PendingParts = newPending,
            };

            mpf.Metadata = newMeta;
            _ = SchedulePersistAsync(mpf);
            return newMeta;
        }
    }

    private static long SumResolvedBytes(DavMultipartFile.Meta meta)
    {
        var sum = 0L;
        foreach (var p in meta.FileParts ?? []) sum += p.FilePartByteRange.Count;
        return sum;
    }

    private async Task SchedulePersistAsync(DavMultipartFile mpf)
    {
        var p = _persistors.GetOrAdd(mpf.Id, _ => new Persistor());
        var myStamp = Interlocked.Increment(ref p.LatestStamp);

        await p.Sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref p.LatestStamp) != myStamp) return;
            await BlobStore.WriteBlob(mpf.Id, mpf).ConfigureAwait(false);
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
        }
    }

    public void Dispose()
    {
        _resolutionCache.Dispose();
        _passwordedNetworkHeaderParses.Dispose();
    }
}
