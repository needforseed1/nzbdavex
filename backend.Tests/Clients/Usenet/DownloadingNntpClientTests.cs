using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DownloadingNntpClientTests
{
    [Fact]
    public async Task FullQueueCapacityDefersDirectlyToProviderPools()
    {
        var inner = new DeferredReleaseClient();
        var config = BuildConfig(maxQueueConnections: null);
        using var client = new DownloadingNntpClient(inner, config);

        var responses = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(i => client.DecodedArticleAsync($"segment-{i}", CancellationToken.None)));

        Assert.Equal(3, inner.Started);
        foreach (var response in responses) await response.Stream.DisposeAsync();
        inner.ReleaseAll();
    }

    [Fact]
    public async Task LowerQueueCapacityStillLimitsDownloads()
    {
        var inner = new DeferredReleaseClient();
        var config = BuildConfig(maxQueueConnections: 1);
        using var client = new DownloadingNntpClient(inner, config);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var first = await client.DecodedArticleAsync("segment-1", timeout.Token);
        var secondTask = client.DecodedArticleAsync("segment-2", timeout.Token);
        await Task.Delay(25, timeout.Token);
        Assert.Equal(1, inner.Started);

        inner.ReleaseNext();
        var second = await secondTask;
        Assert.Equal(2, inner.Started);
        inner.ReleaseAll();
        await first.Stream.DisposeAsync();
        await second.Stream.DisposeAsync();
    }

    private static ConfigManager BuildConfig(int? maxQueueConnections)
    {
        var providerConfig = new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "news.example",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "pass",
                    MaxConnections = 2,
                },
            ],
        };
        var values = new List<ConfigItem>
        {
            new()
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(providerConfig),
            },
        };
        if (maxQueueConnections.HasValue)
        {
            values.Add(new ConfigItem
            {
                ConfigName = "usenet.max-queue-connections",
                ConfigValue = maxQueueConnections.Value.ToString(),
            });
        }

        var config = new ConfigManager();
        config.UpdateValues(values);
        return config;
    }

    private sealed class DeferredReleaseClient : NntpClient
    {
        private readonly Queue<Action<ArticleBodyResult>> _callbacks = new();
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public void ReleaseNext()
        {
            Action<ArticleBodyResult> callback;
            lock (_callbacks) callback = _callbacks.Dequeue();
            callback(ArticleBodyResult.Retrieved);
        }

        public void ReleaseAll()
        {
            while (true)
            {
                Action<ArticleBodyResult>? callback;
                lock (_callbacks)
                    callback = _callbacks.Count > 0 ? _callbacks.Dequeue() : null;
                if (callback is null) return;
                callback(ArticleBodyResult.Retrieved);
            }
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
            => DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            if (callback is not null)
            {
                lock (_callbacks) _callbacks.Enqueue(callback);
            }

            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article follows",
                ArticleHeaders = new UsenetArticleHeader { Headers = new Dictionary<string, string>() },
                Stream = new YencStream(new MemoryStream()),
            });
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
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
