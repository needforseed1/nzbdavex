using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueItemProcessor(
    QueueItem queueItem,
    Stream? queueNzbStream,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    ProviderUsageTracker providerUsageTracker,
    WatchdogLog watchdogLog,
    QueueItemSourceTracker sourceTracker,
    IProgress<int> progress,
    ConcurrentDictionary<Guid, int> retryAttempts,
    Action firstSegmentsCompleted,
    CancellationToken ct
)
{
    private const int MaxProviderRetryAttempts = 20;
    private int? _prepDurationMs;
    private int? _healthDurationMs;

    private static TimeSpan GetProviderRetryBackoff(int attempt)
    {
        var seconds = Math.Min(60d, 10d * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    internal static bool ShouldDeferHealthCheck(int processorCount, int processorConcurrency)
    {
        return processorCount > processorConcurrency;
    }

    public async Task ProcessAsync()
    {
        // initialize
        var startTime = DateTime.Now;
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");

        using var providerScope = providerUsageTracker.BeginScope(queueItem.Id);

        // process the job
        try
        {
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (e.GetBaseException().IsCancellationException())
        {
            Log.Information($"Processing of queue item `{queueItem.JobName}` was cancelled.");
            dbClient.Ctx.ClearChangeTracker();
        }

        catch (Exception e) when (e.IsRetryableDownloadException())
        {
            try
            {
                var attempt = retryAttempts.AddOrUpdate(queueItem.Id, 1, (_, prev) => prev + 1);
                if (attempt > MaxProviderRetryAttempts)
                {
                    Log.Error($"Giving up on `{queueItem.JobName}` after {attempt - 1} provider-connection " +
                              $"failures -- {e.Message}");
                    await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
                    return;
                }

                var backoff = GetProviderRetryBackoff(attempt);
                Log.Warning($"Provider connection issue for `{queueItem.JobName}` " +
                            $"(attempt {attempt}/{MaxProviderRetryAttempts}); retrying in {backoff.TotalSeconds:0}s -- {e.Message}");
                dbClient.Ctx.ClearChangeTracker();
                queueItem.PauseUntil = DateTime.Now + backoff;
                dbClient.Ctx.QueueItems.Attach(queueItem);
                dbClient.Ctx.Entry(queueItem).Property(x => x.PauseUntil).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
            }
            catch (Exception ex)
            {
                Log.Error(ex, ex.Message);
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception e)
        {
            try
            {
                await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(e, ex.Message);
            }
        }
    }

    private async Task ProcessQueueItemAsync(DateTime startTime)
    {
        // if the `/blobs` folder is tampered with outside the nzbdav process,
        // then it is possible that the nzb file goes missing.
        if (queueNzbStream is null)
            throw new Exception($"The NZB file could not be found.");

        // load config for handling duplicate nzbs
        var existingMountFolder = await GetMountFolder().ConfigureAwait(false);
        var duplicateNzbBehavior = configManager.GetDuplicateNzbBehavior();

        // if the mount folder already exists and setting is `marked-failed`
        // then immediately mark the job as failed.
        var isDuplicateNzb = existingMountFolder is not null;
        if (isDuplicateNzb && duplicateNzbBehavior == "mark-failed")
        {
            const string error = "Duplicate nzb: the download folder for this nzb already exists.";
            await MarkQueueItemCompleted(startTime, error, () => Task.FromResult(existingMountFolder))
                .ConfigureAwait(false);
            return;
        }

        // read the nzb document
        var nzb = await NzbDocument.LoadAsync(queueNzbStream).ConfigureAwait(false);
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();

        // Look for a password in filename and nzb document
        // The file name's password takes priority, as an easy override
        var archivePassword = FilenameUtil.GetNzbPassword(queueItem.FileName) ??
            nzb.Metadata.GetValueOrDefault("password");

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/nzbdav-dev/nzbdav/issues/101
        var articlesToPrecheck = nzbFiles.SelectMany(x => x.Segments).Select(x => x.MessageId);
        HealthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);

        // step 1 -- get name and size of each nzb file
        var stepTimer = Stopwatch.StartNew();
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var prepConnections = Math.Min(nzbFiles.Count, configManager.GetMaxQueueConnections());
        var queuedMs = Math.Max(0, (long)(DateTime.Now - queueItem.CreatedAt).TotalMilliseconds);
        Log.Information(
            "queue-stage nzo={NzoId} job={JobName} stage=first-segments start files={Files} connections={Connections} queuedMs={QueuedMs}",
            queueItem.Id, queueItem.JobName, nzbFiles.Count, prepConnections, queuedMs);
        List<FetchFirstSegmentsStep.NzbFileWithFirstSegment> segments;
        try
        {
            segments = await FetchFirstSegmentsStep.FetchFirstSegments(
                nzbFiles, usenetClient, configManager, ct, part1Progress).ConfigureAwait(false);
        }
        finally
        {
            firstSegmentsCompleted();
        }
        var msFirstSeg = stepTimer.ElapsedMilliseconds;
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=first-segments done ms={ElapsedMs}",
            queueItem.Id, queueItem.JobName, msFirstSeg);
        stepTimer.Restart();
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=par2 start",
            queueItem.Id, queueItem.JobName);
        var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
            segments, usenetClient, ct).ConfigureAwait(false);
        var msPar2 = stepTimer.ElapsedMilliseconds;
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=par2 done ms={ElapsedMs}",
            queueItem.Id, queueItem.JobName, msPar2);
        stepTimer.Restart();
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);

        // step 2a -- try altmount-style lazy RAR mounting for the rar group
        // when enabled. On success, the entire rar group is handled here
        // (only the first volume gets parsed) and skipped in step 2b. On
        // ineligibility — multi-file, compressed, solid, or first-volume
        // parse failure — fall through to the per-part eager pipeline.
        LazyRarProcessor.Result? lazyRarResult = null;
        var rarFiles = fileInfos.Where(x => GetGroupName(x) == "rar").ToList();
        if (configManager.IsLazyRarParsingEnabled() && rarFiles.Count > 0)
        {
            Log.Information("queue-stage nzo={NzoId} job={JobName} stage=lazy-rar start files={Files}",
                queueItem.Id, queueItem.JobName, rarFiles.Count);
            var lazyProc = new LazyRarProcessor(rarFiles, usenetClient, configManager, archivePassword, ct);
            lazyRarResult = await lazyProc.ProcessAsync().ConfigureAwait(false) as LazyRarProcessor.Result;
        }
        var msRar = stepTimer.ElapsedMilliseconds;
        if (rarFiles.Count > 0)
            Log.Information("queue-stage nzo={NzoId} job={JobName} stage=lazy-rar done ms={ElapsedMs} mounted={Mounted}",
                queueItem.Id, queueItem.JobName, msRar, lazyRarResult is not null);
        stepTimer.Restart();

        // step 2b -- per-file processing for everything else (and for the
        // rar group when lazy mounting was skipped or unsupported).
        var skipRarGroup = lazyRarResult is not null;
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword, skipRarGroup).ToList();
        var part2Progress = progress
            .Offset(50)
            .Scale(50, 100)
            .ToMultiProgress(fileProcessors.Count);
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=processors start processors={Processors}",
            queueItem.Id, queueItem.JobName, fileProcessors.Count);
        var processorConcurrency = configManager.GetMaxQueueConnections() + 5;
        var fileProcessingTask = fileProcessors
            .Select(x => x!.ProcessAsync(part2Progress.SubProgress))
            .WithConcurrencyAsync(processorConcurrency)
            .GetAllAsync(ct);

        // Step 3 can overlap a processor set that fits in one concurrency wave.
        // When processors are already queued behind that wave, starting the full
        // STAT workload can starve both workloads and trip provider timeouts.
        var checkedFullHealth = false;
        var healthArticleCount = 0;
        Task? healthCheckTask = null;
        Stopwatch? healthTimer = null;
        DeferredProgress? deferredHealthProgress = null;
        using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var healthCheckCategories = configManager.GetEnsureArticleExistenceCategories();
        var shouldCheckHealth = healthCheckCategories.Contains(queueItem.Category.ToLower());
        var articlesToCheck = shouldCheckHealth
            ? fileInfos
                .Where(x => x.IsRar || FilenameUtil.IsImportantFileType(x.FileName))
                .SelectMany(x => x.NzbFile.GetSegmentIds())
                .ToList()
            : [];
        var deferHealthCheck = shouldCheckHealth &&
            ShouldDeferHealthCheck(fileProcessors.Count, processorConcurrency);

        void StartHealthCheck(bool overlapsProcessors)
        {
            healthArticleCount = articlesToCheck.Count;
            if (overlapsProcessors)
            {
                Log.Information(
                    "queue-stage nzo={NzoId} job={JobName} stage=health start articles={Articles} overlap=processors",
                    queueItem.Id, queueItem.JobName, articlesToCheck.Count);
            }
            else
            {
                Log.Information(
                    "queue-stage nzo={NzoId} job={JobName} stage=health start articles={Articles} " +
                    "overlap=none reason=processor-backlog",
                    queueItem.Id, queueItem.JobName, articlesToCheck.Count);
            }
            deferredHealthProgress = new DeferredProgress(progress
                .Offset(100)
                .ToPercentage(articlesToCheck.Count));
            if (!overlapsProcessors) deferredHealthProgress.Enable();
            var healthCheckConcurrency = configManager.GetHealthCheckConnections();
            var healthPipelineDepth = configManager.GetHealthPipeliningDepth();
            var healthPipelineLanes = Math.Min(healthCheckConcurrency, configManager.GetHealthPipeliningLanes());
            Log.Information(
                "queue-stage nzo={NzoId} job={JobName} stage=health mode={Mode} depth={Depth} lanes={Lanes}",
                queueItem.Id, queueItem.JobName,
                configManager.IsHealthPipeliningEnabled() ? "pipelined-stat" : "parallel-stat",
                configManager.IsHealthPipeliningEnabled() ? healthPipelineDepth : 1,
                configManager.IsHealthPipeliningEnabled() ? healthPipelineLanes : healthCheckConcurrency);
            healthTimer = Stopwatch.StartNew();
            var healthWorkTask = configManager.IsHealthPipeliningEnabled()
                ? usenetClient.CheckAllSegmentsPipelinedAsync(articlesToCheck, healthPipelineDepth,
                    healthPipelineLanes, deferredHealthProgress, healthCts.Token)
                : usenetClient.CheckAllSegmentsAsync(articlesToCheck, healthCheckConcurrency,
                    deferredHealthProgress, healthCts.Token);
            healthCheckTask = StopTimerWhenCompleted(healthWorkTask, healthTimer);
        }

        if (shouldCheckHealth && !deferHealthCheck) StartHealthCheck(overlapsProcessors: true);

        List<BaseProcessor.Result?> fileProcessingResultsAll;
        try
        {
            fileProcessingResultsAll = await fileProcessingTask.ConfigureAwait(false);
        }
        catch
        {
            await healthCts.CancelAsync().ConfigureAwait(false);
            if (healthCheckTask is not null)
            {
                try
                {
                    await healthCheckTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the processor exception that caused the cancellation.
                }
            }

            throw;
        }

        var fileProcessingResults = fileProcessingResultsAll
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
        if (lazyRarResult is not null) fileProcessingResults.Add(lazyRarResult);
        var msProcessors = stepTimer.ElapsedMilliseconds;
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=processors done ms={ElapsedMs} results={Results}",
            queueItem.Id, queueItem.JobName, msProcessors, fileProcessingResults.Count);
        stepTimer.Restart();

        // Do not let overlapped health reports move the queue progress into the
        // health range before the processor phase has reached 100%.
        deferredHealthProgress?.Enable();
        if (deferHealthCheck) StartHealthCheck(overlapsProcessors: false);
        var healthWaitTimer = Stopwatch.StartNew();
        if (healthCheckTask is not null)
        {
            await healthCheckTask.ConfigureAwait(false);
            checkedFullHealth = true;
        }
        var msHealthWait = healthWaitTimer.ElapsedMilliseconds;
        var msHealth = healthTimer?.ElapsedMilliseconds ?? 0;
        _prepDurationMs = ToDurationMs(msFirstSeg + msPar2 + msRar + msProcessors);
        _healthDurationMs = checkedFullHealth ? ToDurationMs(msHealth) : null;
        if (checkedFullHealth)
        {
            var statRate = msHealth > 0
                ? (int)Math.Round(healthArticleCount * 1000d / msHealth)
                : healthArticleCount;
            Log.Information(
                "queue-stage nzo={NzoId} job={JobName} stage=health done ms={ElapsedMs} waitMs={WaitMs} " +
                "rate={StatRate}stat/s",
                queueItem.Id, queueItem.JobName, msHealth, msHealthWait, statRate);
        }
        stepTimer.Stop();

        Log.Information(
            "play-timing nzo={NzoId} files={Files} firstSeg={FirstSeg}ms par2={Par2}ms rar={Rar}ms " +
            "processors={Processors}ms health={Health}ms healthWait={HealthWait}ms",
            queueItem.Id, nzbFiles.Count, msFirstSeg, msPar2, msRar, msProcessors, msHealth, msHealthWait);

        // update the database
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=database start",
            queueItem.Id, queueItem.JobName);
        await MarkQueueItemCompleted(startTime, error: null, async () =>
        {
            var categoryFolder = await GetOrCreateCategoryFolder().ConfigureAwait(false);
            var mountFolder = await CreateMountFolder(categoryFolder, existingMountFolder, duplicateNzbBehavior)
                .ConfigureAwait(false);
            new RarAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new FileAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);

            // post-processing
            new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
            new BlocklistedFilePostProcessor(configManager, dbClient).RemoveBlocklistedFiles();

            // validate video files found
            if (configManager.IsEnsureImportableVideoEnabled())
                new EnsureImportableVideoValidator(dbClient).ThrowIfValidationFails();

            // create strm files, if necessary
            if (configManager.GetImportStrategy() == "strm")
                await new CreateStrmFilesPostProcessor(configManager, dbClient).CreateStrmFilesAsync()
                    .ConfigureAwait(false);

            return mountFolder;
        }).ConfigureAwait(false);
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=database done",
            queueItem.Id, queueItem.JobName);
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword,
        bool skipRarGroup = false
    )
    {
        var groups = fileInfos
            .GroupBy(GetGroupName);

        foreach (var group in groups)
        {
            if (group.Key == "7z")
                yield return new SevenZipProcessor(group.ToList(), usenetClient, configManager, archivePassword, ct);

            else if (group.Key == "rar")
            {
                if (skipRarGroup) continue;
                foreach (var fileInfo in group)
                    yield return new RarProcessor(fileInfo, usenetClient, archivePassword, ct);
            }

            else if (group.Key == "multipart-mkv")
                yield return new MultipartMkvProcessor(group.ToList(), usenetClient, ct);

            else if (group.Key == "other")
                foreach (var fileInfo in group)
                    yield return new FileProcessor(fileInfo, usenetClient, ct);
        }
    }

    private static string GetGroupName(GetFileInfosStep.FileInfo x) =>
        FilenameUtil.Is7zFile(x.FileName) ? "7z"
        : x.IsRar || FilenameUtil.IsRarFile(x.FileName) ? "rar"
        : FilenameUtil.IsMultipartMkv(x.FileName) ? "multipart-mkv"
        : "other";

    private async Task<DavItem?> GetMountFolder()
    {
        var query = from mountFolder in dbClient.Ctx.Items
            join categoryFolder in dbClient.Ctx.Items on mountFolder.ParentId equals categoryFolder.Id
            where mountFolder.Name == queueItem.JobName
                  && mountFolder.ParentId != null
                  && categoryFolder.Name == queueItem.Category
                  && categoryFolder.ParentId == DavItem.ContentFolder.Id
            select mountFolder;

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<DavItem> GetOrCreateCategoryFolder()
    {
        // if the category item already exists, return it
        var categoryFolder = await dbClient.GetDirectoryChildAsync(
            DavItem.ContentFolder.Id, queueItem.Category, ct).ConfigureAwait(false);
        if (categoryFolder is not null)
            return categoryFolder;

        // otherwise, create it
        categoryFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: DavItem.ContentFolder,
            name: queueItem.Category,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(categoryFolder);
        return categoryFolder;
    }

    private Task<DavItem> CreateMountFolder
    (
        DavItem categoryFolder,
        DavItem? existingMountFolder,
        string duplicateNzbBehavior
    )
    {
        if (existingMountFolder is not null && duplicateNzbBehavior == "increment")
            return IncrementMountFolder(categoryFolder);

        var mountFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: categoryFolder,
            name: queueItem.JobName,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: queueItem.Id,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(mountFolder);
        return Task.FromResult(mountFolder);
    }

    private async Task<DavItem> IncrementMountFolder(DavItem categoryFolder)
    {
        for (var i = 2; i < 100; i++)
        {
            var name = $"{queueItem.JobName} ({i})";
            var existingMountFolder =
                await dbClient.GetDirectoryChildAsync(categoryFolder.Id, name, ct).ConfigureAwait(false);
            if (existingMountFolder is not null) continue;

            var mountFolder = DavItem.New(
                id: Guid.NewGuid(),
                parent: categoryFolder,
                name: name,
                fileSize: null,
                type: DavItem.ItemType.Directory,
                subType: DavItem.ItemSubType.Directory,
                releaseDate: null,
                lastHealthCheck: null,
                historyItemId: queueItem.Id,
                fileBlobId: null
            );
            dbClient.Ctx.Items.Add(mountFolder);
            return mountFolder;
        }

        throw new Exception("Duplicate nzb with more than 100 existing copies.");
    }

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, DateTime jobStartTime, string? errorMessage = null)
    {
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = DateTime.Now,
            FileName = queueItem.FileName,
            JobName = queueItem.JobName,
            Category = queueItem.Category,
            DownloadStatus = errorMessage == null
                ? HistoryItem.DownloadStatusOption.Completed
                : HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = queueItem.TotalSegmentBytes,
            DownloadTimeSeconds = (int)(DateTime.Now - jobStartTime).TotalSeconds,
            PrepDurationMs = _prepDurationMs,
            HealthDurationMs = _healthDurationMs,
            FailMessage = errorMessage,
            DownloadDirId = mountFolder?.Id,
            NzbBlobId = queueItem.Id,
            IndexerName = queueItem.IndexerName,
            ContentGroupKey = queueItem.ContentGroupKey,
        };
    }

    private async Task MarkQueueItemCompleted
    (
        DateTime startTime,
        string? error = null,
        Func<Task<DavItem?>>? databaseOperations = null
    )
    {
        dbClient.Ctx.ClearChangeTracker();
        var mountFolder = databaseOperations != null ? await databaseOperations.Invoke().ConfigureAwait(false) : null;
        var historyItem = CreateHistoryItem(mountFolder, startTime, error);
        var configuredProviders = configManager.GetUsenetProviderConfig().Providers;
        var providerUsage = ProviderUsageTracker.ToDisplayHosts(
            providerUsageTracker.Snapshot(queueItem.Id), configuredProviders);
        var nicknamesByHost = configuredProviders
            .Where(p => !string.IsNullOrWhiteSpace(p.Nickname))
            .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Nickname, StringComparer.OrdinalIgnoreCase);
        var historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(
            historyItem, mountFolder, configManager, providerUsage, nicknamesByHost);
        dbClient.Ctx.QueueItems.Remove(queueItem);
        dbClient.Ctx.HistoryItems.Add(historyItem);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemAdded, historySlot.ToJson());
        _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"]);
        _ = RefreshMonitoredDownloads();
        RecordWatchdogAttemptIfExternal(startTime, error, providerUsage);
        retryAttempts.TryRemove(queueItem.Id, out _);
    }

    // Emits a Watchdog attempt entry for queue items that didn't come through
    // ProfilePlayController (which writes its own attempts already). Lets users
    // see third-party SAB-compatible client / Sonarr enqueues with provider attribution
    // on the /watchdog page.
    private void RecordWatchdogAttemptIfExternal(
        DateTime startTime,
        string? error,
        IReadOnlyDictionary<string, long> providerUsage)
    {
        if (sourceTracker.ConsumeIsProfileFlow(queueItem.Id)) return;
        if (!configManager.IsPlaybackWatchdogEnabled()) return;

        var attemptedAt = new DateTimeOffset(startTime.ToUniversalTime(), TimeSpan.Zero);
        var durationMs = (int)Math.Max(0, (DateTime.Now - startTime).TotalMilliseconds);
        var outcome = error == null
            ? WatchdogEntry.Outcome.QueueCompleted
            : WatchdogEntry.Outcome.QueueFailed;
        var providerHost = FormatProviders(providerUsage);

        watchdogLog.Record(new WatchdogEntry
        {
            ClickId = queueItem.Id,
            AttemptedAt = attemptedAt,
            ContentType = string.IsNullOrEmpty(queueItem.Category) ? "unknown" : queueItem.Category,
            RequestedTitle = queueItem.JobName ?? queueItem.FileName,
            CandidateTitle = queueItem.JobName ?? queueItem.FileName,
            IndexerName = queueItem.IndexerName ?? "—",
            Size = queueItem.TotalSegmentBytes,
            RankIndex = 0,
            Result = outcome,
            FailReason = error,
            DurationMs = durationMs,
            PrepDurationMs = _prepDurationMs,
            HealthDurationMs = _healthDurationMs,
            IsWinner = error == null,
            ProviderHost = providerHost,
            QueueItemId = queueItem.Id,
            ContentGroupKey = queueItem.ContentGroupKey,
        });
    }

    private static int ToDurationMs(long durationMs) =>
        (int)Math.Clamp(durationMs, 0, int.MaxValue);

    private static Task StopTimerWhenCompleted(Task task, Stopwatch timer)
    {
        return task.ContinueWith(
                static (completed, state) =>
                {
                    ((Stopwatch)state!).Stop();
                    return completed;
                },
                timer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default)
            .Unwrap();
    }

    private static string? FormatProviders(IReadOnlyDictionary<string, long> usage)
    {
        if (usage.Count == 0) return null;
        var total = usage.Values.Sum();
        if (total == 0) return string.Join(", ", usage.Keys);
        return string.Join(", ", usage
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} ({(int)Math.Round(100.0 * kv.Value / total)}%)"));
    }

    private async Task RefreshMonitoredDownloads()
    {
        var tasks = configManager
            .GetArrConfig()
            .GetArrClients()
            .Select(RefreshMonitoredDownloads);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RefreshMonitoredDownloads(ArrClient arrClient)
    {
        try
        {
            var downloadClients = await arrClient.GetDownloadClientsAsync().ConfigureAwait(false);
            if (downloadClients.All(x => x.Category != queueItem.Category)) return;
            var queueCount = await arrClient.GetQueueCountAsync().ConfigureAwait(false);
            if (queueCount < 300) await arrClient.RefreshMonitoredDownloads().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug($"Could not refresh monitored downloads for Arr instance: `{arrClient.Host}`. {e.Message}");
        }
    }

    private sealed class DeferredProgress(IProgress<int> target) : IProgress<int>
    {
        private int _enabled;
        private int _latest;

        public void Report(int value)
        {
            Interlocked.Exchange(ref _latest, value);
            if (Volatile.Read(ref _enabled) != 0) target.Report(value);
        }

        public void Enable()
        {
            if (Interlocked.Exchange(ref _enabled, 1) == 0)
                target.Report(Volatile.Read(ref _latest));
        }
    }
}
