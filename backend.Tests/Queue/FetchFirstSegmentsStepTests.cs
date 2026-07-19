using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Queue;

public class FetchFirstSegmentsStepTests
{
    [Fact]
    public async Task SingleTransientFailureRetriesOnlyThatFileNotTheCompletedOnes()
    {
        var files = Enumerable.Range(0, 5).Select(NzbFile).ToList();
        var client = new FlakyFirstSegmentClient(
            failuresBySegment: new() { ["file-3-segment"] = 1 });

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            files, client, new ConfigManager(), CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, result => Assert.False(result.MissingFirstSegment));
        Assert.Equal(2, client.Attempts["file-3-segment"]);
        foreach (var other in files.Where(x => x.Subject != "file-3"))
            Assert.Equal(1, client.Attempts[other.Segments[0].MessageId]);
    }

    [Fact]
    public async Task PersistentRetryableFailureStillFailsTheStepAfterBoundedAttempts()
    {
        var files = new List<NzbFile> { NzbFile(0) };
        var client = new FlakyFirstSegmentClient(
            failuresBySegment: new() { ["file-0-segment"] = int.MaxValue });

        await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(() =>
            FetchFirstSegmentsStep.FetchFirstSegments(
                files, client, new ConfigManager(), CancellationToken.None));

        Assert.Equal(3, client.Attempts["file-0-segment"]);
    }

    [Fact]
    public async Task MissingArticleIsNotRetried()
    {
        var files = new List<NzbFile> { NzbFile(0) };
        var client = new FlakyFirstSegmentClient(
            missingSegments: ["file-0-segment"]);

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            files, client, new ConfigManager(), CancellationToken.None);

        Assert.True(Assert.Single(results).MissingFirstSegment);
        Assert.Equal(1, client.Attempts["file-0-segment"]);
    }

    private static NzbFile NzbFile(int index)
    {
        var file = new NzbFile { Subject = $"file-{index}" };
        file.Segments.Add(new NzbSegment
        {
            Bytes = 1,
            MessageId = $"file-{index}-segment",
        });
        return file;
    }

    private sealed class FlakyFirstSegmentClient(
        Dictionary<string, int>? failuresBySegment = null,
        IReadOnlyCollection<string>? missingSegments = null) : NntpClient
    {
        private readonly ConcurrentDictionary<string, int> _remainingFailures =
            new(failuresBySegment ?? []);

        public ConcurrentDictionary<string, int> Attempts { get; } = new();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            var id = segmentId.ToString();
            Attempts.AddOrUpdate(id, 1, (_, previous) => previous + 1);

            if (missingSegments?.Contains(id) == true)
                throw new UsenetArticleNotFoundException(id);

            var shouldFail = false;
            _remainingFailures.AddOrUpdate(
                id,
                _ => 0,
                (_, remaining) =>
                {
                    shouldFail = remaining > 0;
                    return Math.Max(0, remaining - 1);
                });
            if (shouldFail)
                throw new CouldNotConnectToUsenetException(
                    "No Usenet provider could complete the operation.",
                    new TimeoutException("Provider did not become usable."));

            // A minimal valid yEnc body: bytes {1, 2, 3} encode to "+,-".
            var body = "=ybegin line=128 size=3 name=test\r\n+,-\r\n=yend size=3\r\n"u8.ToArray();
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article follows",
                ArticleHeaders = new UsenetArticleHeader { Headers = new Dictionary<string, string>() },
                Stream = new YencStream(new MemoryStream(body)),
            });
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
            => DecodedArticleAsync(segmentId, cancellationToken);

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
