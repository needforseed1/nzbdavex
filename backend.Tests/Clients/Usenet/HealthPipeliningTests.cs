using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class HealthPipeliningTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(96)]
    public async Task ConfiguredLanesAreIndependentOfPipelineDepth(int lanes)
    {
        var client = new LaneCountingClient();
        var segments = Enumerable.Range(0, 352).Select(x => $"segment-{x}").ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var check = client.CheckAllSegmentsPipelinedAsync(
            segments, depth: 32, fallbackConcurrency: lanes, progress: null, timeout.Token);

        while (client.StartedLanes < lanes && !timeout.IsCancellationRequested)
            await Task.Delay(10, timeout.Token);

        client.ReleaseLanes();
        await check;

        Assert.Equal(lanes, client.MaxConcurrentLanes);
    }

    [Fact]
    public async Task CompletedLanesStealWorkFromBlockedLane()
    {
        var client = new LaneCountingClient(blockFirstLaneOnly: true);
        var segments = Enumerable.Range(0, 80).Select(x => $"segment-{x}").ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var check = client.CheckAllSegmentsPipelinedAsync(
            segments, depth: 8, fallbackConcurrency: 4, progress: null, timeout.Token);

        while (client.ProcessedSegments < 72 && !timeout.IsCancellationRequested)
            await Task.Delay(10, timeout.Token);

        Assert.Equal(72, client.ProcessedSegments);
        client.ReleaseLanes();
        await check;
        Assert.Equal(80, client.ProcessedSegments);
    }

    private sealed class LaneCountingClient : NntpClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockFirstLaneOnly;
        private int _activeLanes;
        private int _startedLanes;
        private int _maxConcurrentLanes;
        private int _processedSegments;

        public LaneCountingClient(bool blockFirstLaneOnly = false)
        {
            _blockFirstLaneOnly = blockFirstLaneOnly;
        }

        public int StartedLanes => Volatile.Read(ref _startedLanes);
        public int MaxConcurrentLanes => Volatile.Read(ref _maxConcurrentLanes);
        public int ProcessedSegments => Volatile.Read(ref _processedSegments);

        public void ReleaseLanes() => _release.TrySetResult();

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeLanes);
            var started = Interlocked.Increment(ref _startedLanes);
            UpdateMaximum(active);
            try
            {
                if (!_blockFirstLaneOnly || started == 1)
                    await _release.Task.WaitAsync(cancellationToken);
                foreach (var segmentId in segmentIds)
                {
                    Interlocked.Increment(ref _processedSegments);
                    yield return new PipelinedStatResult { SegmentId = segmentId, Exists = true };
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeLanes);
            }
        }

        private void UpdateMaximum(int value)
        {
            var observed = Volatile.Read(ref _maxConcurrentLanes);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref _maxConcurrentLanes, value, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
