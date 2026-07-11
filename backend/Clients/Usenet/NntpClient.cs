using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Abstract base class for NNTP clients with default implementations of utility methods.
/// </summary>
public abstract class NntpClient : INntpClient
{
    public virtual int PipeliningDepth => 0;

    public abstract Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken);

    public abstract Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken);

    public abstract Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken);

    public abstract Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken);

    public abstract Task<UsenetDateResponse> DateAsync(
        CancellationToken cancellationToken);

    public virtual void CloseIdleConnections(string? host = null)
    {
    }

    public abstract void Dispose();

    public virtual Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support acquiring exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedBodyAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken
    )
    {
        var message = $"{GetType().Name} does not support DecodedArticleAsync with exclusive connections.";
        throw new NotSupportedException(message);
    }

    public virtual async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        var decodedBodyResponse = await DecodedBodyAsync(segmentId, ct).ConfigureAwait(false);
        await using var stream = decodedBodyResponse.Stream;
        var headers = await stream.GetYencHeadersAsync(ct).ConfigureAwait(false);
        return headers!;
    }

    public virtual async Task<long> GetFileSizeAsync(NzbFile file, CancellationToken ct)
    {
        if (file.Segments.Count == 0) return 0;
        var headers = await GetYencHeadersAsync(file.Segments[^1].MessageId, ct).ConfigureAwait(false);
        return headers!.PartOffset + headers!.PartSize;
    }

    public virtual async Task<NzbFileStream> GetFileStream(NzbFile nzbFile, int articleBufferSize, CancellationToken ct)
    {
        var segmentIds = nzbFile.GetSegmentIds();
        var fileSize = await GetFileSizeAsync(nzbFile, ct).ConfigureAwait(false);
        return new NzbFileStream(segmentIds, fileSize, this, articleBufferSize);
    }

    public virtual NzbFileStream GetFileStream(NzbFile nzbFile, long fileSize, int articleBufferSize)
    {
        return new NzbFileStream(nzbFile.GetSegmentIds(), fileSize, this, articleBufferSize);
    }

    public virtual NzbFileStream GetFileStream(string[] segmentIds, long fileSize, int articleBufferSize)
    {
        return new NzbFileStream(segmentIds, fileSize, this, articleBufferSize);
    }

    public virtual async Task CheckAllSegmentsAsync
    (
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        using var childCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = childCt.Token;

        var tasks = segmentIds
            .Select(async segmentId => (
                SegmentId: segmentId,
                Result: await StatAsync(segmentId, token).ConfigureAwait(false)
            ))
            .WithConcurrencyAsync(concurrency);

        var processed = 0;
        await foreach (var task in tasks.ConfigureAwait(false))
        {
            progress?.Report(++processed);
            if (task.Result.ResponseType == UsenetResponseType.ArticleExists) continue;
            await childCt.CancelAsync().ConfigureAwait(false);
            throw new UsenetArticleNotFoundException(task.SegmentId);
        }
    }

    public virtual async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var segmentId in segmentIds)
        {
            var response = await StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
            yield return new PipelinedStatResult
            {
                SegmentId = segmentId,
                Exists = response.ResponseType == UsenetResponseType.ArticleExists,
            };
        }
    }

    public virtual async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var segmentId in segmentIds)
        {
            PipelinedBodyResult result;
            try
            {
                var response = await DecodedBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
                result = new PipelinedBodyResult { SegmentId = segmentId, Found = true, Stream = response.Stream };
            }
            catch (UsenetArticleNotFoundException)
            {
                result = new PipelinedBodyResult { SegmentId = segmentId, Found = false, Stream = null };
            }

            yield return result;
        }
    }

    public virtual async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        foreach (var segmentId in segmentIds)
        {
            PipelinedArticleResult result;
            try
            {
                var response = await DecodedArticleAsync(segmentId, cancellationToken).ConfigureAwait(false);
                result = new PipelinedArticleResult
                {
                    SegmentId = segmentId,
                    Found = true,
                    Stream = response.Stream,
                    ArticleHeaders = response.ArticleHeaders,
                };
            }
            catch (UsenetArticleNotFoundException)
            {
                result = new PipelinedArticleResult { SegmentId = segmentId, Found = false };
            }

            yield return result;
        }
    }

    public virtual async Task CheckAllSegmentsPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) return;
        var lanes = ResolvePipelinedStatLanes(segmentIds.Count, fallbackConcurrency);
        if (lanes > 1)
        {
            using var childCt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = childCt.Token;
            var processedCount = 0;
            var nextSegment = 0;
            var schedulerLock = new object();
            var batchDepth = Math.Clamp(depth, 1, 64);

            IReadOnlyList<string>? TakeNextBatch()
            {
                lock (schedulerLock)
                {
                    if (nextSegment >= segmentIds.Count) return null;

                    var remaining = segmentIds.Count - nextSegment;
                    // Keep roughly two waves of work available. Full-depth batches
                    // dominate the run, then shrink near completion so fast lanes can
                    // absorb work from slow providers instead of waiting on stragglers.
                    var targetBatchSize = Math.Max(1, (remaining + lanes * 2 - 1) / (lanes * 2));
                    var count = Math.Min(remaining, Math.Min(batchDepth, targetBatchSize));
                    var batch = new string[count];
                    for (var i = 0; i < count; i++) batch[i] = segmentIds[nextSegment + i];
                    nextSegment += count;
                    return batch;
                }
            }

            var laneTasks = Enumerable.Range(0, lanes)
                .Select(_ => CheckPipelinedStatLaneAsync(TakeNextBatch, batchDepth, progress, childCt, token, () =>
                    Interlocked.Increment(ref processedCount)))
                .ToArray();

            try
            {
                await Task.WhenAll(laneTasks).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (TryRethrowMissingArticle(e))
            {
                throw;
            }
        }

        var processed = 0;
        var anyMissing = false;
        await foreach (var result in StatsPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (result.Exists)
            {
                progress?.Report(++processed);
                continue;
            }

            anyMissing = true;
            break;
        }

        if (anyMissing)
        {
            var verifiedPrefix = processed;
            var remaining = segmentIds.Skip(verifiedPrefix);
            var fallbackProgress = progress == null
                ? null
                : new InlineProgress<int>(x => progress.Report(verifiedPrefix + x));
            await CheckAllSegmentsAsync(remaining, fallbackConcurrency, fallbackProgress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task CheckPipelinedStatLaneAsync(
        Func<IReadOnlyList<string>?> takeNextBatch,
        int depth,
        IProgress<int>? progress,
        CancellationTokenSource childCt,
        CancellationToken cancellationToken,
        Func<int> incrementProcessed)
    {
        try
        {
            while (takeNextBatch() is { } segmentIds)
            {
                await foreach (var result in StatsPipelinedAsync(segmentIds, depth, cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (result.Exists)
                    {
                        progress?.Report(incrementProcessed());
                        continue;
                    }

                    await childCt.CancelAsync().ConfigureAwait(false);
                    throw new UsenetArticleNotFoundException(result.SegmentId);
                }
            }
        }
        catch
        {
            await childCt.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static int ResolvePipelinedStatLanes(int segmentCount, int fallbackConcurrency)
    {
        return Math.Max(1, Math.Min(fallbackConcurrency, segmentCount));
    }

    private static bool TryRethrowMissingArticle(Exception e)
    {
        if (!e.TryGetCausingException(out UsenetArticleNotFoundException? missing) || missing is null) return false;
        ExceptionDispatchInfo.Capture(missing).Throw();
        return true;
    }

}
