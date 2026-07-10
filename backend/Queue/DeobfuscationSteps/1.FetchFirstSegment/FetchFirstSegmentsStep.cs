// ReSharper disable InconsistentNaming

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

public static class FetchFirstSegmentsStep
{
    private const int TailRetryMinimumFiles = 64;
    private const int TailRetryMaximumRemaining = 8;
    private static readonly TimeSpan TailStallThreshold = TimeSpan.FromSeconds(2);

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
        var concurrency = configManager.GetMaxQueueConnections() + 5;
        var pending = new Queue<NzbFile>(files);
        var running = new Dictionary<Task<NzbFileWithFirstSegment>, FirstSegmentWork>();
        var results = new List<NzbFileWithFirstSegment>(files.Count);
        var completed = 0;
        var tailRetryEnabled = files.Count >= TailRetryMinimumFiles;

        FirstSegmentWork Start(NzbFile file, bool isRetry)
        {
            var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var task = FetchFirstSegment(file, usenetClient, attemptCts.Token);
            return new FirstSegmentWork(file, task, attemptCts, isRetry);
        }

        void AddResult(NzbFileWithFirstSegment result)
        {
            results.Add(result);
            progress?.Report(++completed);
        }

        void FillAvailableSlots()
        {
            while (pending.Count > 0 && running.Count < concurrency)
            {
                var work = Start(pending.Dequeue(), isRetry: false);
                running.Add(work.Task, work);
            }
        }

        try
        {
            FillAvailableSlots();
            while (running.Count > 0)
            {
                var retryableTail = tailRetryEnabled &&
                                    pending.Count == 0 &&
                                    running.Count <= TailRetryMaximumRemaining &&
                                    running.Values.Any(x => !x.IsRetry);
                Task completedTask;
                if (retryableTail)
                {
                    var stallTimer = Task.Delay(TailStallThreshold, cancellationToken);
                    completedTask = await Task.WhenAny(running.Keys.Cast<Task>().Append(stallTimer))
                        .ConfigureAwait(false);
                    if (ReferenceEquals(completedTask, stallTimer))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var stalled = running.Values.Where(x => !x.IsRetry).ToList();
                        Log.Information(
                            "prep-first-segment tail stalled remaining={Remaining} retrying={Retrying} stallMs={StallMs}",
                            running.Count, stalled.Count, (int)TailStallThreshold.TotalMilliseconds);

                        foreach (var work in stalled) work.AttemptCts.Cancel();
                        foreach (var work in stalled)
                        {
                            running.Remove(work.Task);
                            try
                            {
                                AddResult(await work.Task.ConfigureAwait(false));
                            }
                            catch (OperationCanceledException) when (
                                work.AttemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            {
                                var retry = Start(work.File, isRetry: true);
                                running.Add(retry.Task, retry);
                            }
                            finally
                            {
                                work.AttemptCts.Dispose();
                            }
                        }

                        continue;
                    }
                }
                else
                {
                    completedTask = await Task.WhenAny(running.Keys).ConfigureAwait(false);
                }

                var completedWork = running[(Task<NzbFileWithFirstSegment>)completedTask];
                running.Remove(completedWork.Task);
                try
                {
                    AddResult(await completedWork.Task.ConfigureAwait(false));
                }
                finally
                {
                    completedWork.AttemptCts.Dispose();
                }

                FillAvailableSlots();
            }

            return results;
        }
        finally
        {
            foreach (var work in running.Values) work.AttemptCts.Cancel();
            foreach (var work in running.Values)
            {
                try
                {
                    await work.Task.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the exception or cancellation already leaving this method.
                }
                finally
                {
                    work.AttemptCts.Dispose();
                }
            }
        }
    }

    private sealed record FirstSegmentWork(
        NzbFile File,
        Task<NzbFileWithFirstSegment> Task,
        CancellationTokenSource AttemptCts,
        bool IsRetry);

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
        catch (UsenetArticleNotFoundException)
        {
            return BuildMissingFirstSegment(nzbFile);
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
