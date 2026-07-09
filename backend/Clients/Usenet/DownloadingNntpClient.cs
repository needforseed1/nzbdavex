using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is only responsible for limiting download operations (BODY/ARTICLE)
/// to the configured number of maximum download connections.
/// </summary>
/// <param name="usenetClient"></param>
public class DownloadingNntpClient : WrappingNntpClient
{
    private readonly ConfigManager _configManager;
    private readonly PrioritizedSemaphore _streamingSemaphore;
    private readonly PrioritizedSemaphore _queueSemaphore;
    private volatile bool _limitQueueDownloads;

    public DownloadingNntpClient(INntpClient usenetClient, ConfigManager configManager) : base(usenetClient)
    {
        var maxDownloadConnections = configManager.GetMaxDownloadConnections();
        var maxQueueConnections = configManager.GetMaxQueueConnections();
        var streamingPriority = configManager.GetStreamingPriority();
        _configManager = configManager;
        _streamingSemaphore = new PrioritizedSemaphore(maxDownloadConnections, maxDownloadConnections, streamingPriority);
        _queueSemaphore = new PrioritizedSemaphore(maxQueueConnections, maxQueueConnections);
        _limitQueueDownloads = maxQueueConnections < configManager.GetUsenetProviderConfig().TotalPooledConnections;
        configManager.OnConfigChanged += OnConfigChanged;
    }

    public override int PipeliningDepth =>
        _configManager.IsPlaybackPipeliningEnabled() ? _configManager.GetPipeliningDepth() : 0;

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        if (e.ChangedConfig.ContainsKey("usenet.max-download-connections"))
            _streamingSemaphore.UpdateMaxAllowed(_configManager.GetMaxDownloadConnections());

        if (e.ChangedConfig.ContainsKey("usenet.max-queue-connections")
            || e.ChangedConfig.ContainsKey("usenet.max-download-connections")
            || e.ChangedConfig.ContainsKey("usenet.providers"))
        {
            _queueSemaphore.UpdateMaxAllowed(_configManager.GetMaxQueueConnections());
            _limitQueueDownloads = _configManager.GetMaxQueueConnections()
                                   < _configManager.GetUsenetProviderConfig().TotalPooledConnections;
        }

        if (e.ChangedConfig.ContainsKey("usenet.streaming-priority"))
            _streamingSemaphore.UpdatePriorityOdds(_configManager.GetStreamingPriority());
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var semaphore = await AcquireExclusiveConnectionAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        return await base.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken).ConfigureAwait(false);

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            semaphore.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain, CancellationToken cancellationToken)
    {
        var semaphore = await AcquireExclusiveConnectionAsync(onConnectionReadyAgain, cancellationToken).ConfigureAwait(false);
        return await base.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken)
            .ConfigureAwait(false);

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            semaphore.Release();
            onConnectionReadyAgain?.Invoke(articleBodyResult);
        }
    }

    private async Task<DownloadPermit> AcquireExclusiveConnectionAsync(Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AcquireExclusiveConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }
    }

    private async Task<DownloadPermit> AcquireExclusiveConnectionAsync(CancellationToken cancellationToken)
    {
        var downloadPriorityContext = cancellationToken.GetContext<DownloadPriorityContext>();
        var semaphorePriority = downloadPriorityContext?.Priority ?? SemaphorePriority.Low;
        if (semaphorePriority == SemaphorePriority.Low && !_limitQueueDownloads)
            return default;

        var semaphore = semaphorePriority == SemaphorePriority.High ? _streamingSemaphore : _queueSemaphore;
        await semaphore.WaitAsync(semaphorePriority, cancellationToken).ConfigureAwait(false);
        return new DownloadPermit(semaphore);
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var semaphore = await AcquireExclusiveConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new UsenetExclusiveConnection(_ => semaphore.Release());
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return base.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection, CancellationToken cancellationToken)
    {
        var onConnectionReadyAgain = exclusiveConnection.OnConnectionReadyAgain;
        return base.DecodedArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);
    }

    public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var priority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
        var semaphore = priority == SemaphorePriority.High ? _streamingSemaphore : _queueSemaphore;
        await semaphore.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var result in base.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var priority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
        var semaphore = priority == SemaphorePriority.High ? _streamingSemaphore : _queueSemaphore;
        await semaphore.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var result in base.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly struct DownloadPermit(PrioritizedSemaphore? semaphore)
    {
        public void Release() => semaphore?.Release();
    }
}
