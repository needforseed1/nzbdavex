using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
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
    ConcurrentDictionary<Guid, int> unverifiableAttempts,
    Action firstSegmentsCompleted,
    CancellationToken ct
)
{
    // Retry a transient provider failure observed after preparation once, then
    // fail into History/Watchdog. Preparation itself deliberately gets no
    // whole-job retry because each first-segment attempt walks all providers.
    private const int MaxProviderFailureAttempts = 2;
    // Unverifiable results (providers unavailable, article presence unknown)
    // get a much tighter bound than connection failures: one bounded retry,
    // then fail into history/watchdog with an explicit unverifiable message.
    // They must never cycle through the 20-attempt PauseUntil loop.
    private const int MaxUnverifiableAttempts = 2;
    private static readonly TimeSpan UnverifiableRetryBackoff = TimeSpan.FromSeconds(60);
    private const int HealthPrimeSegmentCount = 16;
    private static readonly TimeSpan HealthWarmupHandoffGrace = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HealthWarmupCleanupGrace = TimeSpan.FromSeconds(1);
    private int? _prepDurationMs;
    private int? _healthDurationMs;
    private int? _healthWaitDurationMs;
    private bool _preparationCompleted;

    private static TimeSpan GetProviderRetryBackoff(int attempt)
    {
        var seconds = Math.Min(60d, 10d * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    internal static bool ShouldRetryWholeQueueItem(Exception exception, bool preparationCompleted) =>
        preparationCompleted && exception.IsRetryableDownloadException();

    internal static bool HasExhaustedProviderFailureBudget(int failureCount) =>
        failureCount >= MaxProviderFailureAttempts;

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
        using var recoveryNoticeCapture = providerUsageTracker.BeginRecoveryNoticeCapture(notice =>
            _ = websocketManager.SendMessage(
                WebsocketTopic.QueueItemRecoveryNotice,
                $"{queueItem.Id}|{(notice is null ? string.Empty : notice.ToJson())}"));
        providerUsageTracker.ReportRecoveryNotice(null);

        // process the job
        try
        {
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (IsCallerCancellation(e, ct))
        {
            Log.Information($"Processing of queue item `{queueItem.JobName}` was cancelled.");
            dbClient.Ctx.ClearChangeTracker();
        }

        catch (UsenetArticleUnverifiableException e)
        {
            try
            {
                var attempt = unverifiableAttempts.AddOrUpdate(queueItem.Id, 1, (_, prev) => prev + 1);
                if (attempt >= MaxUnverifiableAttempts)
                {
                    Log.Error(
                        $"Giving up on `{queueItem.JobName}` after {attempt} unverifiable " +
                        $"health checks -- {e.Message}");
                    await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
                    return;
                }

                Log.Warning(
                    $"Health check for `{queueItem.JobName}` was unverifiable " +
                    $"(attempt {attempt}/{MaxUnverifiableAttempts}); retrying in " +
                    $"{UnverifiableRetryBackoff.TotalSeconds:0}s -- {e.Message}");
                dbClient.Ctx.ClearChangeTracker();
                queueItem.PauseUntil = DateTime.Now + UnverifiableRetryBackoff;
                dbClient.Ctx.QueueItems.Attach(queueItem);
                dbClient.Ctx.Entry(queueItem).Property(x => x.PauseUntil).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
                providerUsageTracker.ReportRecoveryNotice(null);
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
            }
            catch (Exception ex)
            {
                Log.Error(ex, ex.Message);
            }
        }

        catch (Exception e) when (ShouldRetryWholeQueueItem(e, _preparationCompleted))
        {
            try
            {
                var attempt = retryAttempts.AddOrUpdate(queueItem.Id, 1, (_, prev) => prev + 1);
                if (HasExhaustedProviderFailureBudget(attempt))
                {
                    Log.Error($"Giving up on `{queueItem.JobName}` after {attempt} provider-connection " +
                              $"failures -- {e.Message}");
                    await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
                    return;
                }

                var backoff = GetProviderRetryBackoff(attempt);
                Log.Warning($"Provider connection issue for `{queueItem.JobName}` " +
                            $"(attempt {attempt}/{MaxProviderFailureAttempts}); retrying in {backoff.TotalSeconds:0}s -- {e.Message}");
                dbClient.Ctx.ClearChangeTracker();
                queueItem.PauseUntil = DateTime.Now + backoff;
                dbClient.Ctx.QueueItems.Attach(queueItem);
                dbClient.Ctx.Entry(queueItem).Property(x => x.PauseUntil).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
                providerUsageTracker.ReportRecoveryNotice(null);
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

        var healthCheckCategories = configManager.GetEnsureArticleExistenceCategories();
        var shouldCheckHealth = healthCheckCategories.Contains(queueItem.Category.ToLower());
        var connectionWarmer = shouldCheckHealth ? usenetClient as IQueueConnectionWarmer : null;
        using var healthConnectionWarmupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task healthConnectionWarmupTask = connectionWarmer is not null
            ? PrewarmHealthConnectionsDuringPrepAsync(
                connectionWarmer, queueItem, healthConnectionWarmupCts.Token)
            : Task.CompletedTask;
        await using var healthWarmupLifetime = new HealthWarmupLifetime(
            healthConnectionWarmupCts,
            () => healthConnectionWarmupTask,
            queueItem.JobName);

        // read the nzb document while dedicated health pools validate in parallel
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
        var healthPrimeSegmentIds = shouldCheckHealth && configManager.IsHealthPipeliningEnabled()
            ? SelectHealthPrimeSegmentIds(nzbFiles, HealthPrimeSegmentCount)
            : [];
        var healthPrimeDepth = Math.Min(
            HealthPrimeSegmentCount, configManager.GetHealthPipeliningDepth());
        if (connectionWarmer is not null && healthPrimeSegmentIds.Count > 0)
            healthConnectionWarmupTask = PrimeHealthConnectionsDuringPrepAsync(
                connectionWarmer,
                healthConnectionWarmupTask,
                healthPrimeSegmentIds,
                healthPrimeDepth,
                queueItem,
                healthConnectionWarmupCts.Token);

        // step 1 -- get name and size of each nzb file
        var stepTimer = Stopwatch.StartNew();
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var prepConnections = FetchFirstSegmentsStep.ResolveConcurrency(
            nzbFiles.Count, configManager.GetMaxQueueConnections());
        var queuedMs = Math.Max(0, (long)(DateTime.Now - queueItem.CreatedAt).TotalMilliseconds);
        var usageBeforeFirstSegments = providerUsageTracker.Snapshot(queueItem.Id);
        var bytesBeforeFirstSegments = providerUsageTracker.SnapshotBytes(queueItem.Id);
        var attemptsBeforeFirstSegments = providerUsageTracker.SnapshotPrepAttempts(queueItem.Id);
        var failoversBeforeFirstSegments = providerUsageTracker.GetFailoverSaves(queueItem.Id);
        Log.Information(
            "queue-stage nzo={NzoId} job={JobName} stage=first-segments start files={Files} connections={Connections} queuedMs={QueuedMs}",
            queueItem.Id, queueItem.JobName, nzbFiles.Count, prepConnections, queuedMs);
        List<FetchFirstSegmentsStep.NzbFileWithFirstSegment> segments;
        using (providerUsageTracker.BeginByteCapture())
        using (providerUsageTracker.BeginPrepAttemptCapture())
        {
            try
            {
                segments = await FetchFirstSegmentsStep.FetchFirstSegments(
                    nzbFiles, usenetClient, configManager, ct, part1Progress).ConfigureAwait(false);
            }
            catch
            {
                var elapsedMs = stepTimer.ElapsedMilliseconds;
                var partialProviders = BuildPrepProviderStats(
                    usageBeforeFirstSegments,
                    providerUsageTracker.Snapshot(queueItem.Id),
                    bytesBeforeFirstSegments,
                    providerUsageTracker.SnapshotBytes(queueItem.Id),
                    attemptsBeforeFirstSegments,
                    providerUsageTracker.SnapshotPrepAttempts(queueItem.Id));
                var partialFallbacks = Math.Max(0,
                    providerUsageTracker.GetFailoverSaves(queueItem.Id) - failoversBeforeFirstSegments);
                _prepDurationMs = ToDurationMs(elapsedMs);
                providerUsageTracker.RecordPrepStats(new PrepUsageSnapshot(
                    nzbFiles.Count, prepConnections, queuedMs, elapsedMs, 0, 0, 0,
                    false, partialFallbacks, partialProviders, "first-segments"));
                throw;
            }
            finally
            {
                firstSegmentsCompleted();
            }
        }
        if (connectionWarmer is not null)
            healthConnectionWarmupTask = Task.WhenAll(
                healthConnectionWarmupTask,
                PrewarmPrimaryHealthConnectionsAfterPrepAsync(
                    connectionWarmer,
                    healthPrimeSegmentIds,
                    healthPrimeDepth,
                    queueItem,
                    healthConnectionWarmupCts.Token));
        var msFirstSeg = stepTimer.ElapsedMilliseconds;
        var usageAfterFirstSegments = providerUsageTracker.Snapshot(queueItem.Id);
        var bytesAfterFirstSegments = providerUsageTracker.SnapshotBytes(queueItem.Id);
        var firstSegmentProviders = BuildPrepProviderStats(
            usageBeforeFirstSegments, usageAfterFirstSegments,
            bytesBeforeFirstSegments, bytesAfterFirstSegments,
            attemptsBeforeFirstSegments, providerUsageTracker.SnapshotPrepAttempts(queueItem.Id));
        var firstSegmentFallbacks = Math.Max(0,
            providerUsageTracker.GetFailoverSaves(queueItem.Id) - failoversBeforeFirstSegments);
        providerUsageTracker.ReportRecoveryNotice(firstSegmentFallbacks > 0
            ? new QueueRecoveryNotice("prep", "recovered", (int)Math.Min(int.MaxValue, firstSegmentFallbacks))
            : null);
        void RecordPrepProgress(string lastStage, long par2Ms = 0, long rarMs = 0,
            long processorsMs = 0, bool lazyRarMounted = false)
        {
            _prepDurationMs = ToDurationMs(msFirstSeg + par2Ms + rarMs + processorsMs);
            providerUsageTracker.RecordPrepStats(new PrepUsageSnapshot(
                nzbFiles.Count, prepConnections, queuedMs, msFirstSeg, par2Ms, rarMs, processorsMs,
                lazyRarMounted, firstSegmentFallbacks, firstSegmentProviders, lastStage));
        }
        RecordPrepProgress("first-segments");
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=first-segments done ms={ElapsedMs}",
            queueItem.Id, queueItem.JobName, msFirstSeg);
        stepTimer.Restart();
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=par2 start",
            queueItem.Id, queueItem.JobName);
        List<NzbWebDAV.Par2Recovery.Packets.FileDesc> par2FileDescriptors;
        long msPar2;
        try
        {
            par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
                segments, usenetClient, ct).ConfigureAwait(false);
        }
        finally
        {
            msPar2 = stepTimer.ElapsedMilliseconds;
            RecordPrepProgress("par2", msPar2);
        }
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
        long msRar;
        try
        {
            if (configManager.IsLazyRarParsingEnabled() && rarFiles.Count > 0)
            {
                Log.Information("queue-stage nzo={NzoId} job={JobName} stage=lazy-rar start files={Files}",
                    queueItem.Id, queueItem.JobName, rarFiles.Count);
                var lazyProc = new LazyRarProcessor(rarFiles, usenetClient, configManager, archivePassword, ct);
                lazyRarResult = await lazyProc.ProcessAsync().ConfigureAwait(false) as LazyRarProcessor.Result;
            }
        }
        finally
        {
            msRar = stepTimer.ElapsedMilliseconds;
            RecordPrepProgress("rar", msPar2, msRar, lazyRarMounted: lazyRarResult is not null);
        }
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
        var processorConcurrency = GetProcessorConcurrency(configManager.GetMaxQueueConnections());
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
            if (!overlapsProcessors && connectionWarmer is not null)
            {
                // Deferred start: the prep-time warm bursts above the persistent
                // floor decay after one minute of idleness, so a long processor
                // backlog leaves the pools at floor level. Re-burst both pools on
                // the still-live warmup CTS; the grace handoff below still starts
                // health as soon as useful capacity exists and lets growth
                // continue in parallel.
                healthConnectionWarmupTask = Task.WhenAll(
                    PrewarmHealthConnectionsDuringPrepAsync(
                        connectionWarmer, queueItem, healthConnectionWarmupCts.Token),
                    PrewarmPrimaryHealthConnectionsAfterPrepAsync(
                        connectionWarmer, healthPrimeSegmentIds, healthPrimeDepth,
                        queueItem, healthConnectionWarmupCts.Token));
            }
            var healthWorkTask = RunHealthCheckAfterWarmupAsync(
                healthConnectionWarmupTask,
                healthConnectionWarmupCts,
                ct,
                () => configManager.IsHealthPipeliningEnabled()
                    ? usenetClient.CheckAllSegmentsPipelinedAsync(articlesToCheck, healthPipelineDepth,
                        healthPipelineLanes, deferredHealthProgress, healthCts.Token)
                    : usenetClient.CheckAllSegmentsAsync(articlesToCheck, healthCheckConcurrency,
                        deferredHealthProgress, healthCts.Token),
                () => Log.Information(
                    "queue-stage nzo={NzoId} job={JobName} stage=health-warmup " +
                    "handoff=grace-expired graceMs={GraceMs}",
                    queueItem.Id, queueItem.JobName, HealthWarmupHandoffGrace.TotalMilliseconds));
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
            RecordPrepProgress("processors", msPar2, msRar, stepTimer.ElapsedMilliseconds,
                lazyRarResult is not null);
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
                finally
                {
                    _healthDurationMs = ToDurationMs(healthTimer?.ElapsedMilliseconds ?? 0);
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
        RecordPrepProgress("processors", msPar2, msRar, msProcessors, lazyRarResult is not null);
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=processors done ms={ElapsedMs} results={Results}",
            queueItem.Id, queueItem.JobName, msProcessors, fileProcessingResults.Count);
        stepTimer.Restart();

        // Do not let overlapped health reports move the queue progress into the
        // health range before the processor phase has reached 100%.
        deferredHealthProgress?.Enable();
        if (deferHealthCheck) StartHealthCheck(overlapsProcessors: false);
        // Preparation provider failures go straight to Watchdog. Only failures
        // observed from the subsequent health phase retain the bounded queue
        // retry policy.
        _preparationCompleted = true;
        var healthWaitTimer = Stopwatch.StartNew();
        if (healthCheckTask is not null)
        {
            try
            {
                await healthCheckTask.ConfigureAwait(false);
                checkedFullHealth = true;
            }
            catch
            {
                RecordPrepProgress("health", msPar2, msRar, msProcessors, lazyRarResult is not null);
                throw;
            }
            finally
            {
                _healthDurationMs = ToDurationMs(healthTimer?.ElapsedMilliseconds ?? 0);
                _healthWaitDurationMs = ToDurationMs(healthWaitTimer.ElapsedMilliseconds);
            }
        }
        var msHealthWait = healthWaitTimer.ElapsedMilliseconds;
        var msHealth = healthTimer?.ElapsedMilliseconds ?? 0;
        _prepDurationMs = ToDurationMs(msFirstSeg + msPar2 + msRar + msProcessors);
        _healthDurationMs = checkedFullHealth ? ToDurationMs(msHealth) : _healthDurationMs;
        _healthWaitDurationMs = checkedFullHealth ? ToDurationMs(msHealthWait) : _healthWaitDurationMs;
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
        RecordPrepProgress("import", msPar2, msRar, msProcessors, lazyRarResult is not null);
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

    internal static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested &&
               exception.IsCancellationException();
    }

    internal static async Task CancelAndObserveWarmupAsync(
        Task warmupTask,
        CancellationTokenSource warmupCancellation,
        string jobName,
        TimeSpan? cleanupGrace = null)
    {
        if (!warmupTask.IsCompleted)
        {
            await warmupCancellation.CancelAsync().ConfigureAwait(false);
            var grace = cleanupGrace ?? HealthWarmupCleanupGrace;
            if (await Task.WhenAny(warmupTask, Task.Delay(grace)).ConfigureAwait(false) != warmupTask)
            {
                Log.Debug(
                    "Health connection warmup cleanup exceeded {GraceMs}ms for {JobName}; " +
                    "observing late completion without blocking the queue.",
                    grace.TotalMilliseconds, jobName);
                _ = ObserveLateWarmupCompletionAsync(warmupTask);
                return;
            }
        }

        try
        {
            await warmupTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (warmupCancellation.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Log.Debug(e, "Health connection warmup cleanup failed for {JobName}.", jobName);
        }
    }

    private sealed class HealthWarmupLifetime(
        CancellationTokenSource cancellation,
        Func<Task> task,
        string jobName) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return new ValueTask(CancelAndObserveWarmupAsync(
                task(), cancellation, jobName));
        }
    }

    private static async Task PrewarmHealthConnectionsDuringPrepAsync(
        IQueueConnectionWarmer connectionWarmer,
        QueueItem queueItem,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=health-prewarm start",
            queueItem.Id, queueItem.JobName);
        try
        {
            await connectionWarmer.PrewarmHealthCheckAsync(cancellationToken).ConfigureAwait(false);
            Log.Information(
                "queue-stage nzo={NzoId} job={JobName} stage=health-prewarm done ms={ElapsedMs}",
                queueItem.Id, queueItem.JobName, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Health connection prewarm cancelled for {JobName}.", queueItem.JobName);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Health connection prewarm failed for {JobName}.", queueItem.JobName);
        }
    }

    private static async Task PrimeHealthConnectionsDuringPrepAsync(
        IQueueConnectionWarmer connectionWarmer,
        Task prewarmTask,
        IReadOnlyList<string> segmentIds,
        int depth,
        QueueItem queueItem,
        CancellationToken cancellationToken)
    {
        await prewarmTask.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return;
        var timer = Stopwatch.StartNew();
        Log.Information(
            "queue-stage nzo={NzoId} job={JobName} stage=health-prime start segments={Segments} depth={Depth}",
            queueItem.Id, queueItem.JobName, segmentIds.Count, depth);
        try
        {
            await connectionWarmer.PrimeHealthCheckAsync(segmentIds, depth, cancellationToken)
                .ConfigureAwait(false);
            Log.Information(
                "queue-stage nzo={NzoId} job={JobName} stage=health-prime done ms={ElapsedMs}",
                queueItem.Id, queueItem.JobName, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Health connection prime cancelled for {JobName}.", queueItem.JobName);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Health connection prime failed for {JobName}.", queueItem.JobName);
        }
    }

    internal static async Task RunHealthCheckAfterWarmupAsync(
        Task warmupTask,
        CancellationTokenSource warmupCancellation,
        CancellationToken operationCancellation,
        Func<Task> runHealthCheck,
        Action? onGraceExpired = null,
        TimeSpan? handoffGrace = null)
    {
        if (!warmupTask.IsCompleted)
        {
            var grace = handoffGrace ?? HealthWarmupHandoffGrace;
            var graceTask = Task.Delay(grace, operationCancellation);
            if (await Task.WhenAny(warmupTask, graceTask).ConfigureAwait(false) != warmupTask)
            {
                onGraceExpired?.Invoke();
                await warmupCancellation.CancelAsync().ConfigureAwait(false);
                if (!warmupTask.IsCompleted)
                {
                    // Foreground health must never remain behind a provider operation
                    // that is slow to observe cancellation. The shared connection
                    // budget still bounds the late cleanup while foreground lanes get
                    // priority for any capacity that is already available.
                    _ = ObserveLateWarmupCompletionAsync(warmupTask);
                    operationCancellation.ThrowIfCancellationRequested();
                    await runHealthCheck().ConfigureAwait(false);
                    return;
                }
            }
        }

        try
        {
            await warmupTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            warmupCancellation.IsCancellationRequested &&
            !operationCancellation.IsCancellationRequested)
        {
            // Prep already produced useful capacity. Stop speculative warmup
            // cleanly and hand the remaining growth to foreground health lanes.
        }

        operationCancellation.ThrowIfCancellationRequested();
        await runHealthCheck().ConfigureAwait(false);
    }

    private static async Task ObserveLateWarmupCompletionAsync(Task warmupTask)
    {
        try
        {
            await warmupTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected after the foreground handoff.
        }
        catch (Exception e)
        {
            Log.Debug(e, "Late health connection warmup cleanup failed.");
        }
    }

    private static async Task PrewarmPrimaryHealthConnectionsAfterPrepAsync(
        IQueueConnectionWarmer connectionWarmer,
        IReadOnlyList<string> primeSegmentIds,
        int primeDepth,
        QueueItem queueItem,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        Log.Information("queue-stage nzo={NzoId} job={JobName} stage=primary-health-prewarm start",
            queueItem.Id, queueItem.JobName);
        try
        {
            await connectionWarmer.PrewarmPrimaryHealthCheckAsync(cancellationToken).ConfigureAwait(false);
            Log.Information(
                "queue-stage nzo={NzoId} job={JobName} stage=primary-health-prewarm done ms={ElapsedMs}",
                queueItem.Id, queueItem.JobName, timer.ElapsedMilliseconds);
            if (primeSegmentIds.Count == 0 || cancellationToken.IsCancellationRequested) return;

            timer.Restart();
            Log.Information(
                "queue-stage nzo={NzoId} job={JobName} stage=primary-health-prime start " +
                "segments={Segments} depth={Depth}",
                queueItem.Id, queueItem.JobName, primeSegmentIds.Count, primeDepth);
            await connectionWarmer.PrimePrimaryHealthCheckAsync(
                    primeSegmentIds, primeDepth, cancellationToken)
                .ConfigureAwait(false);
            Log.Information(
                "queue-stage nzo={NzoId} job={JobName} stage=primary-health-prime done ms={ElapsedMs}",
                queueItem.Id, queueItem.JobName, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Primary health connection prewarm cancelled for {JobName}.", queueItem.JobName);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Primary health connection prewarm failed for {JobName}.", queueItem.JobName);
        }
    }

    internal static IReadOnlyList<string> SelectHealthPrimeSegmentIds(
        IReadOnlyList<NzbFile> files,
        int targetCount)
    {
        // Sample from the population the full health check will actually cover
        // (RAR + important files) so coverage probes judge providers on relevant
        // articles. Subject filenames are an approximation — exact
        // classification is not available this early — and obfuscated subjects
        // fall back to sampling every file.
        var relevantFiles = files
            .Where(file => FilenameUtil.IsImportantFileType(file.GetSubjectFileName()))
            .ToList();
        if (relevantFiles.Count > 0) files = relevantFiles;

        var total = files.Sum(file => (long)file.Segments.Count);
        var selectedCount = (int)Math.Min(Math.Max(0, targetCount), total);
        if (selectedCount == 0) return [];

        var selectedIndexes = selectedCount == 1
            ? [0L]
            : Enumerable.Range(0, selectedCount)
                .Select(index => (long)index * (total - 1) / (selectedCount - 1))
                .Distinct()
                .ToArray();
        var result = new List<string>(selectedIndexes.Length);
        var articleIndex = 0L;
        var selectedIndex = 0;
        foreach (var file in files)
        foreach (var segment in file.Segments)
        {
            if (articleIndex == selectedIndexes[selectedIndex])
            {
                result.Add(segment.MessageId);
                selectedIndex++;
                if (selectedIndex == selectedIndexes.Length) return result;
            }
            articleIndex++;
        }

        return result;
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword,
        bool skipRarGroup = false
    )
    {
        var blocklistedFiles = configManager.GetBlocklistedFiles();
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
                    if (ShouldProcessNonArchiveFile(fileInfo.FileName, blocklistedFiles))
                        yield return new FileProcessor(fileInfo, usenetClient, ct);
        }
    }

    internal static bool ShouldProcessNonArchiveFile(
        string fileName,
        HashSet<string> blocklistedFiles)
    {
        // Important files retain their existing processing path. For everything
        // else, avoid fetching metadata for files that would be removed by the
        // blocklist immediately after aggregation. PAR2 inspection/deobfuscation
        // has already completed before this method is reached.
        return FilenameUtil.IsImportantFileType(fileName)
               || !BlocklistedFilePostProcessor.MatchesAnyPattern(fileName, blocklistedFiles);
    }

    private static string GetGroupName(GetFileInfosStep.FileInfo x) =>
        FilenameUtil.Is7zFile(x.FileName) ? "7z"
        : x.IsRar || FilenameUtil.IsRarFile(x.FileName) ? "rar"
        : FilenameUtil.IsMultipartMkv(x.FileName) ? "multipart-mkv"
        : "other";

    internal static int GetProcessorConcurrency(int maxQueueConnections) =>
        Math.Min(maxQueueConnections + 5, 128);

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
            HealthWaitDurationMs = _healthWaitDurationMs,
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
        unverifiableAttempts.TryRemove(queueItem.Id, out _);
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
            HealthWaitDurationMs = _healthWaitDurationMs,
            PrepStatsJson = SerializePrepStats(providerUsageTracker.SnapshotPrep(queueItem.Id)),
            HealthStatsJson = SerializeHealthStats(providerUsageTracker.SnapshotHealthCheck(queueItem.Id)),
            IsWinner = error == null,
            ProviderHost = providerHost,
            QueueItemId = queueItem.Id,
            ContentGroupKey = queueItem.ContentGroupKey,
        });
    }

    private static int ToDurationMs(long durationMs) =>
        (int)Math.Clamp(durationMs, 0, int.MaxValue);

    private static IReadOnlyList<PrepProviderStat> BuildPrepProviderStats(
        IReadOnlyDictionary<string, long> usageBefore,
        IReadOnlyDictionary<string, long> usageAfter,
        IReadOnlyDictionary<string, long> bytesBefore,
        IReadOnlyDictionary<string, long> bytesAfter,
        IReadOnlyDictionary<string, PrepProviderAttemptStat> attemptsBefore,
        IReadOnlyDictionary<string, PrepProviderAttemptStat> attemptsAfter) =>
        usageAfter.Keys
            .Union(bytesAfter.Keys)
            .Union(attemptsAfter.Keys)
            .Select(providerId => new PrepProviderStat(
                providerId,
                Math.Max(0, usageAfter.GetValueOrDefault(providerId) - usageBefore.GetValueOrDefault(providerId)),
                Math.Max(0, bytesAfter.GetValueOrDefault(providerId) - bytesBefore.GetValueOrDefault(providerId)),
                Math.Max(0, attemptsAfter.GetValueOrDefault(providerId)?.Attempts -
                    (attemptsBefore.GetValueOrDefault(providerId)?.Attempts ?? 0) ?? 0),
                Math.Max(0, attemptsAfter.GetValueOrDefault(providerId)?.Missing -
                    (attemptsBefore.GetValueOrDefault(providerId)?.Missing ?? 0) ?? 0),
                Math.Max(0, attemptsAfter.GetValueOrDefault(providerId)?.Timeouts -
                    (attemptsBefore.GetValueOrDefault(providerId)?.Timeouts ?? 0) ?? 0),
                Math.Max(0, attemptsAfter.GetValueOrDefault(providerId)?.Errors -
                    (attemptsBefore.GetValueOrDefault(providerId)?.Errors ?? 0) ?? 0),
                Math.Max(0, attemptsAfter.GetValueOrDefault(providerId)?.WorkMs -
                    (attemptsBefore.GetValueOrDefault(providerId)?.WorkMs ?? 0) ?? 0)))
            .Where(stat => stat.Articles > 0 || stat.Bytes > 0 || stat.Attempts > 0)
            .OrderByDescending(stat => stat.Articles)
            .ThenBy(stat => stat.ProviderId)
            .ToArray();

    private static string? SerializeHealthStats(HealthCheckUsageSnapshot? stats) =>
        stats is null ? null : JsonSerializer.Serialize(stats);

    private static string? SerializePrepStats(PrepUsageSnapshot? stats) =>
        stats is null ? null : JsonSerializer.Serialize(stats);

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
