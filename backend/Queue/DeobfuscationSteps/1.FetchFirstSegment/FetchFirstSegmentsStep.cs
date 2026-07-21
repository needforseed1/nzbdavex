// ReSharper disable InconsistentNaming

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

public static class FetchFirstSegmentsStep
{
    // First-segment ARTICLE reads discard their sockets after the metadata
    // prefix. Launching the full configured queue limit against a small ready
    // floor therefore creates a pure authentication storm. Match the maximum
    // burst to two waves of the application's 32-slot connection-creation
    // budget; returned/replacement capacity keeps the queue moving from there.
    private const int MaxConcurrentFirstSegmentFetches = 64;
    public static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegments
    (
        List<NzbFile> nzbFiles,
        INntpClient usenetClient,
        ConfigManager configManager,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null
    )
    {
        var files = nzbFiles.Where(x => x.Segments.Count > 0).ToList();
        var probeClient = usenetClient is ArticleCachingNntpClient cachingClient
            ? cachingClient.FirstSegmentProbeClient
            : usenetClient;
        var concurrency = ResolveConcurrency(files.Count, configManager.GetMaxQueueConnections());
        return await files
            .Select(x => FetchFirstSegment(x, probeClient, cancellationToken))
            .WithConcurrencyAsync(concurrency, drainOnFailure: true)
            .GetAllAsync(cancellationToken, progress).ConfigureAwait(false);
    }

    internal static int ResolveConcurrency(int fileCount, int configuredConnections) =>
        Math.Max(1, Math.Min(
            fileCount,
            Math.Min(MaxConcurrentFirstSegmentFetches, configuredConnections + 5)));

    private static NzbFileWithFirstSegment BuildMissingFirstSegment(NzbFile nzbFile) => new()
    {
        NzbFile = nzbFile,
        First16KB = null,
        Header = null,
        MissingFirstSegment = true,
        ReleaseDate = DateTimeOffset.UtcNow,
    };

    private static async Task<NzbFileWithFirstSegment> FetchFirstSegment
    (
        NzbFile nzbFile,
        INntpClient usenetClient,
        CancellationToken cancellationToken
    )
    {
        // Recovery volumes contain repair blocks, not metadata needed by prep.
        // Keep the NZB file entry intact so it can still be exposed or fetched
        // later, but do not spend a connection downloading its first article.
        // The base/index .par2 is deliberately not skipped: it supplies the
        // file descriptors used to recover obfuscated filenames and sizes.
        if (Par2.IsRecoveryVolumeFileName(nzbFile.GetSubjectFileName()))
            return BuildMissingFirstSegment(nzbFile);

        try
        {
            // A single attempt already walks the configured primary and backup
            // provider chain. Repeating it here only duplicates the same burst
            // and delays a useful Watchdog result when capacity is unavailable.
            return await FetchFirstSegmentOnce(nzbFile, usenetClient, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (
            !e.IsCancellationException() &&
            e.IsRetryableDownloadException())
        {
            var firstSegment = nzbFile.Segments[0].MessageId;
            var message =
                $"Preparation could not verify first segment `{firstSegment}` for " +
                $"`{nzbFile.Subject}` after one provider pass because one or more " +
                $"providers were unavailable. Last provider error: {e.Message}";
            Log.Warning(e, "{Error}", message);
            throw new RetryableDownloadException(message, e);
        }
    }

    private static async Task<NzbFileWithFirstSegment> FetchFirstSegmentOnce
    (
        NzbFile nzbFile,
        INntpClient usenetClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // get the first article stream
            var firstSegment = nzbFile.Segments[0].MessageId;
            var article = await usenetClient.DecodedArticleAsync(firstSegment, cancellationToken).ConfigureAwait(false);
            await using var bodyStream = article.Stream!;

            // read up to the first 16KB from the stream
            var totalRead = 0;
            var buffer = new byte[16 * 1024];
            while (totalRead < buffer.Length)
            {
                var read = await bodyStream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            // determine bytes read
            var first16KB = totalRead < buffer.Length
                ? buffer.AsSpan(0, totalRead).ToArray()
                : buffer;

            // get the yencHeaders
            var yencHeaders = await bodyStream
                .GetYencHeadersAsync(cancellationToken)
                .ConfigureAwait(false);

            // return
            return new NzbFileWithFirstSegment
            {
                NzbFile = nzbFile,
                First16KB = first16KB,
                Header = yencHeaders,
                MissingFirstSegment = false,
                ReleaseDate = article.ArticleHeaders!.Date
            };
        }
        catch (UsenetArticleNotFoundException e)
        {
            var firstSegment = nzbFile.Segments[0].MessageId;
            var message =
                $"Preparation failed: required first segment `{firstSegment}` for " +
                $"`{nzbFile.Subject}` is missing from every available Usenet provider.";
            Log.Error(e, "{Error}", message);
            throw new NonRetryableDownloadException(message, e);
        }
    }

    public class NzbFileWithFirstSegment
    {
        private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static readonly byte[] Rar5Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

        public required NzbFile NzbFile { get; init; }
        public required UsenetYencHeader? Header { get; init; }
        public required byte[]? First16KB { get; init; }
        public required bool MissingFirstSegment { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public bool HasRar4Magic() => HasMagic(Rar4Magic);
        public bool HasRar5Magic() => HasMagic(Rar5Magic);

        private bool HasMagic(byte[] sequence)
        {
            return First16KB?.Length >= sequence.Length &&
                   First16KB.AsSpan(0, sequence.Length).SequenceEqual(sequence);
        }
    }
}
