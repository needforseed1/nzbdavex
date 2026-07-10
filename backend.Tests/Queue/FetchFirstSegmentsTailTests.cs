using System.Diagnostics;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public class FetchFirstSegmentsTailTests
{
    [Fact]
    public async Task StalledFinalFetchIsCancelledAndRetriedOnce()
    {
        const int fileCount = 64;
        var files = Enumerable.Range(0, fileCount)
            .Select(index =>
            {
                var file = new NzbFile { Subject = $"file-{index}.rar" };
                file.Segments.Add(new NzbSegment { Bytes = 750_000, MessageId = $"segment-{index}" });
                return file;
            })
            .ToList();
        var client = new TailStallNntpClient("segment-63");
        var config = BuildConfig(fileCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            files, client, config, timeout.Token);

        Assert.Equal(fileCount, results.Count);
        Assert.Equal(2, client.GetCalls("segment-63"));
        Assert.All(Enumerable.Range(0, fileCount - 1),
            index => Assert.Equal(1, client.GetCalls($"segment-{index}")));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
    }

    private static ConfigManager BuildConfig(int maxConnections)
    {
        var providers = new UsenetProviderConfig
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
                    MaxConnections = maxConnections,
                },
            ],
        };
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem
            {
                ConfigName = "usenet.providers",
                ConfigValue = JsonSerializer.Serialize(providers),
            },
            new ConfigItem
            {
                ConfigName = "usenet.max-queue-connections",
                ConfigValue = maxConnections.ToString(),
            },
        ]);
        return config;
    }

    private sealed class TailStallNntpClient(string stalledSegment) : NntpClient
    {
        private readonly Dictionary<string, int> _calls = new();

        public int GetCalls(string segmentId)
        {
            lock (_calls) return _calls.GetValueOrDefault(segmentId);
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            int call;
            lock (_calls)
            {
                var id = (string)segmentId;
                call = _calls.GetValueOrDefault(id) + 1;
                _calls[id] = call;
            }

            if ((string)segmentId == stalledSegment && call == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            var header = new UsenetYencHeader
            {
                FileName = $"{segmentId}.rar",
                FileSize = 16 * 1024,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartSize = 16 * 1024,
                PartOffset = 0,
            };
            return new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article follows",
                ArticleHeaders = new UsenetArticleHeader { Headers = [] },
                Stream = new CachedYencStream(header, new MemoryStream(new byte[16 * 1024])),
            };
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
