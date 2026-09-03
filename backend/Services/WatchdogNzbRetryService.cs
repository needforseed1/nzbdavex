using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Services;

public sealed class WatchdogNzbRetryService(
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager)
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> RetryLocks = new();
    internal const string SubmissionSourcePrefix = "watchdog-manual-retry:";

    public sealed record Match(
        Guid BlobId,
        string Confidence,
        string SourceStatus,
        string Title,
        string? Indexer,
        string Category,
        long Size,
        long CreatedAtUnix);

    public sealed record Resolution(WatchdogEntry Entry, IReadOnlyList<Match> Matches);
    public sealed record RetryResult(Guid QueueItemId, bool Existing);

    public async Task<Resolution?> ResolveAsync(long eventId, CancellationToken ct)
    {
        var entry = await dbClient.Ctx.WatchdogEntries.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == eventId, ct).ConfigureAwait(false);
        if (entry is null) return null;

        var evidence = new List<Evidence>();
        if (entry.QueueItemId is { } directId)
        {
            evidence.AddRange(await dbClient.Ctx.QueueItems.AsNoTracking()
                .Where(x => x.Id == directId)
                .Select(x => new Evidence(x.Id, true, "Queue", x.JobName, x.IndexerName,
                    x.Category, x.TotalSegmentBytes, x.CreatedAt))
                .ToListAsync(ct).ConfigureAwait(false));
            evidence.AddRange(await dbClient.Ctx.HistoryItems.AsNoTracking()
                .Where(x => x.Id == directId && x.NzbBlobId != null)
                .Select(x => new Evidence(x.NzbBlobId!.Value, true,
                    x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed ? "Completed history" : "Failed history",
                    x.JobName, x.IndexerName, x.Category, x.TotalSegmentBytes, x.CreatedAt))
                .ToListAsync(ct).ConfigureAwait(false));
        }

        if (!string.IsNullOrWhiteSpace(entry.ContentGroupKey))
        {
            evidence.AddRange(await dbClient.Ctx.QueueItems.AsNoTracking()
                .Where(x => x.ContentGroupKey == entry.ContentGroupKey)
                .Select(x => new Evidence(x.Id, false, "Queue", x.JobName, x.IndexerName,
                    x.Category, x.TotalSegmentBytes, x.CreatedAt))
                .ToListAsync(ct).ConfigureAwait(false));
            evidence.AddRange(await dbClient.Ctx.HistoryItems.AsNoTracking()
                .Where(x => x.ContentGroupKey == entry.ContentGroupKey && x.NzbBlobId != null)
                .Select(x => new Evidence(x.NzbBlobId!.Value, false,
                    x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed ? "Completed history" : "Failed history",
                    x.JobName, x.IndexerName, x.Category, x.TotalSegmentBytes, x.CreatedAt))
                .ToListAsync(ct).ConfigureAwait(false));
        }

        var matches = new List<Match>();
        foreach (var group in evidence.GroupBy(x => x.BlobId))
        {
            var available = false;
            await using (var blob = BlobStore.ReadBlob(group.Key)) available = blob is not null;
            if (!available) continue;

            var best = group.OrderByDescending(x => x.Direct).ThenByDescending(x => Score(entry, x)).First();
            var exact = group.Any(x => x.Direct);
            var score = group.Max(x => Score(entry, x));
            matches.Add(new Match(
                group.Key,
                exact ? "exact" : score >= 4 ? "strong" : "plausible",
                best.SourceStatus,
                best.Title,
                best.Indexer,
                best.Category,
                best.Size,
                new DateTimeOffset(DateTime.SpecifyKind(best.CreatedAt, DateTimeKind.Local)).ToUnixTimeSeconds()));
        }

        return new Resolution(entry, matches
            .OrderByDescending(x => x.Confidence == "exact")
            .ThenByDescending(x => x.Confidence == "strong")
            .ThenByDescending(x => x.CreatedAtUnix)
            .ToList());
    }

    public async Task<RetryResult> RetryAsync(long eventId, Guid selectedBlobId, HttpContext httpContext)
    {
        var gate = RetryLocks.GetOrAdd(eventId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(httpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var source = SubmissionSourcePrefix + eventId;
            var existingQueue = await dbClient.Ctx.QueueItems.AsNoTracking()
                .Where(x => x.SubmissionSource == source)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(httpContext.RequestAborted).ConfigureAwait(false);
            if (existingQueue.HasValue) return new RetryResult(existingQueue.Value, true);
            var existingHistory = await dbClient.Ctx.HistoryItems.AsNoTracking()
                .Where(x => x.SubmissionSource == source)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(httpContext.RequestAborted).ConfigureAwait(false);
            if (existingHistory.HasValue) return new RetryResult(existingHistory.Value, true);

            var resolution = await ResolveAsync(eventId, httpContext.RequestAborted).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Watchdog event was not found.");
            var selected = resolution.Matches.SingleOrDefault(x => x.BlobId == selectedBlobId)
                ?? throw new InvalidOperationException("The selected saved NZB does not belong to this Watchdog event.");

            var sourceStillQueued = await dbClient.Ctx.QueueItems.AsNoTracking()
                .AnyAsync(x => x.Id == selectedBlobId, httpContext.RequestAborted).ConfigureAwait(false);
            if (sourceStillQueued)
            {
                var (processing, _) = queueManager.GetInProgressQueueItem();
                var message = processing?.Id == selectedBlobId
                    ? "The source queue item is currently processing."
                    : "The source queue item is still queued.";
                throw new WatchdogRetryBusyException(message);
            }

            var blob = BlobStore.ReadBlob(selectedBlobId)
                ?? throw new WatchdogRetryUnavailableException("The saved NZB is no longer available.");
            var request = new AddFileRequest
            {
                FileName = EnsureNzbExtension(selected.Title),
                ContentType = "application/x-nzb",
                NzbFileStream = blob,
                Category = selected.Category,
                Priority = QueueItem.PriorityOption.Force,
                PostProcessing = QueueItem.PostProcessingOption.None,
                IndexerName = selected.Indexer,
                ContentGroupKey = resolution.Entry.ContentGroupKey,
                SubmissionSource = source,
                CancellationToken = httpContext.RequestAborted,
            };
            var controller = new AddFileController(httpContext, dbClient, queueManager, configManager, websocketManager);
            var response = await controller.AddFileAsync(request).ConfigureAwait(false);
            return new RetryResult(Guid.Parse(response.NzoIds.Single()), false);
        }
        catch (FileNotFoundException)
        {
            throw new WatchdogRetryUnavailableException("The saved NZB is no longer available.");
        }
        finally
        {
            gate.Release();
        }
    }

    public static bool TryParseRetryEventId(string? submissionSource, out long eventId)
    {
        eventId = 0;
        return submissionSource is not null
            && submissionSource.StartsWith(SubmissionSourcePrefix, StringComparison.Ordinal)
            && long.TryParse(submissionSource[SubmissionSourcePrefix.Length..], out eventId);
    }

    private static string EnsureNzbExtension(string title) =>
        title.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) ? title : title + ".nzb";

    private static int Score(WatchdogEntry entry, Evidence evidence)
    {
        var score = 0;
        if (TitlesMatch(entry.CandidateTitle, evidence.Title)) score += 3;
        if (!string.IsNullOrWhiteSpace(entry.IndexerName)
            && string.Equals(entry.IndexerName, evidence.Indexer, StringComparison.OrdinalIgnoreCase)) score += 2;
        if (entry.Size > 0 && evidence.Size > 0)
        {
            var ratio = (double)Math.Min(entry.Size, evidence.Size) / Math.Max(entry.Size, evidence.Size);
            if (ratio >= .95) score += 2;
            else if (ratio >= .8) score++;
        }
        if (Math.Abs((entry.AttemptedAt - new DateTimeOffset(evidence.CreatedAt)).TotalHours) <= 24) score++;
        if (!string.IsNullOrWhiteSpace(entry.ContentType)
            && evidence.Category.Contains(entry.ContentType, StringComparison.OrdinalIgnoreCase)) score++;
        return score;
    }

    private static bool TitlesMatch(string left, string right)
    {
        static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var a = Normalize(left.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) ? left[..^4] : left);
        var b = Normalize(right.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) ? right[..^4] : right);
        return a.Length > 0 && a == b;
    }

    private sealed record Evidence(Guid BlobId, bool Direct, string SourceStatus, string Title,
        string? Indexer, string Category, long Size, DateTime CreatedAt);
}

public sealed class WatchdogRetryBusyException(string message) : Exception(message);
public sealed class WatchdogRetryUnavailableException(string message) : Exception(message);
