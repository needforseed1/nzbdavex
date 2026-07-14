using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// Measures real download speed and latency against a single provider and
/// recommends the smallest connection count that nearly maxes out throughput
/// (the diminishing-returns "knee"), plus whether NNTP pipelining helps and at
/// what depth.
///
/// Safety model — the test never disrupts normal operation or usage accounting:
///   • It opens its own ad-hoc connections via <see cref="UsenetStreamingClient.CreateNewConnection"/>,
///     bypassing the shared connection pool, byte tracker and metrics writer.
///   • It probes a few steps above the configured max but stops the instant the
///     provider refuses another connection (the classic 502 "too many connections"),
///     treating that as the real ceiling.
///   • Every level is byte- and time-bounded, and the whole run honours the
///     caller's cancellation token (closing the modal aborts it cleanly).
/// </summary>
public sealed class UsenetBenchmarkService(WebsocketManager websocketManager, BenchmarkCorpusProvider corpus)
{
    // Rough average decoded article size; used to scale how much data each level
    // transfers so even high connection counts move enough bytes to measure.
    private const long ArticleBytesEstimate = 750_000;

    // Never open more than this many sockets at once, regardless of provider/config.
    private const int HardConnectionCeiling = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<BenchmarkResult> RunAsync(
        UsenetProviderConfig.ConnectionDetails provider,
        int configuredMaxConnections,
        BenchmarkIntensity intensity,
        bool connectionsOnly,
        bool pipeliningOnly,
        bool startupOnly,
        bool healthOnly,
        CancellationToken ct)
    {
        var profile = BenchmarkProfile.For(intensity);
        var result = new BenchmarkResult
        {
            PipeliningOnly = pipeliningOnly,
            StartupOnly = startupOnly,
            HealthOnly = healthOnly,
        };
        var cursor = new int[1]; // shared round-robin index into the segment pool

        // 1) Latency — also doubles as a connectivity/credentials check.
        Report("latency", "Measuring latency…", 5, result, null);
        result.Latency = await MeasureLatencyAsync(provider, profile.LatencySamples, ct).ConfigureAwait(false);

        // 2) Corpus — real message-ids to download.
        Report("corpus", "Gathering test articles…", 12, result, null);
        var pool = await corpus.GetSegmentPoolAsync(profile.MaxCorpusSegments, ct).ConfigureAwait(false);
        if (pool.Count == 0)
        {
            result.ThroughputTested = false;
            result.Warnings.Add(healthOnly
                ? "No stored article ids were available for a mixed STAT benchmark. Import something first, then re-run."
                : "No downloaded articles were available to measure speed, so only latency was tested. " +
                  "Download something first, then re-run to get a connection recommendation.");
            Report("done", "Done — latency only.", 100, result, null);
            return result;
        }

        result.ThroughputTested = true;
        if (!healthOnly && pool.Count < 200)
            result.Warnings.Add("Only a small pool of test articles was available, so speed numbers may be a little noisy.");

        if (healthOnly)
        {
            result.HealthPipelining = await MeasureHealthPipeliningAsync(
                provider, pool, profile, result, ct).ConfigureAwait(false);
            Report("done", "Done.", 100, result, null);
            return result;
        }

        if (startupOnly)
        {
            Report("startup", "Measuring playback startup…", 30, result, null);
            result.Startup = await MeasureStartupAsync(
                provider, configuredMaxConnections, pool, cursor, profile,
                bytes => result.DataUsedBytes += bytes, ct).ConfigureAwait(false);
            if (result.Startup.NonPipelinedFirstMs <= 0)
                result.Warnings.Add(
                    "The leading test article was unavailable, so no playback-pipelining recommendation was made.");
            Report("done", "Done.", 100, result, null);
            return result;
        }

        // Pipelining-only mode: leave the connection count alone and just find
        // the best pipelining depth at the count the user already runs.
        if (pipeliningOnly)
        {
            var conns = Math.Clamp(configuredMaxConnections, 1, HardConnectionCeiling);
            Report("pipelining", $"Testing pipelining at {conns} connection{(conns == 1 ? "" : "s")}…", 30, result, conns);
            result.Pipelining = await MeasurePipeliningAsync(
                provider, conns, pool, cursor, profile, FocusedPipelineDepths(intensity),
                bytes => result.DataUsedBytes += bytes, ct).ConfigureAwait(false);

            if (conns >= 24)
                result.Warnings.Add(
                    "At high connection counts, pipelining usually adds little — running many connections in " +
                    "parallel already hides most of the per-request latency it would otherwise save.");

            Report("done", "Done.", 100, result, null);
            return result;
        }

        // 3) Throughput sweep — climb connection counts until the knee or the cap.
        var levels = BuildLevels(configuredMaxConnections, profile);
        int? providerCap = null;
        for (var i = 0; i < levels.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var level = levels[i];

            if (result.DataUsedBytes >= profile.HardTotalBytes)
            {
                result.Warnings.Add("Reached the data budget before testing every connection level.");
                break;
            }

            Report("sweep", $"Testing {level} connection{(level == 1 ? "" : "s")}…",
                ProgressPercent(15, 75, i, levels.Count), result, level);

            var sample = await MeasureThroughputAsync(
                provider, level, pool, cursor, TargetBytes(level, profile),
                profile.PerLevelMaxDuration, pipeliningDepth: 0, ct).ConfigureAwait(false);
            result.DataUsedBytes += sample.Bytes;

            if (sample.OpenedConnections == 0)
            {
                // Couldn't open a single socket at this level — treat the prior
                // level as the ceiling and stop climbing.
                providerCap ??= Math.Max(1, level - 1);
                break;
            }

            result.Sweep.Add(new BenchmarkSweepPoint
            {
                Connections = sample.OpenedConnections,
                MbPerSec = Math.Round(sample.MbPerSec, 2),
            });
            Report("sweep", $"{sample.OpenedConnections} conn → {sample.MbPerSec:0.0} MB/s",
                ProgressPercent(15, 75, i + 1, levels.Count), result, sample.OpenedConnections);

            if (sample.OpenedConnections < level)
            {
                // Provider refused the full count — that's the real limit.
                providerCap = sample.OpenedConnections;
                result.Warnings.Add(
                    $"Your provider wouldn't allow more than {sample.OpenedConnections} connections at once, " +
                    "so the test stopped there.");
                break;
            }
        }

        result.ProviderConnectionCap = providerCap;
        result.RecommendedConnections = DetectKnee(result.Sweep, providerCap, result.Warnings);

        if (connectionsOnly)
        {
            Report("done", "Done.", 100, result, null);
            return result;
        }

        // 4) Pipelining — compare off vs. a few depths at a moderate concurrency.
        if (result.Sweep.Count > 0 && result.DataUsedBytes < profile.HardTotalBytes)
        {
            var pipeConns = Math.Min(result.RecommendedConnections ?? 1, profile.PipelineTestConnections);
            if (providerCap.HasValue) pipeConns = Math.Min(pipeConns, providerCap.Value);
            pipeConns = Math.Max(1, pipeConns);

            Report("pipelining", "Testing NNTP pipelining…", 88, result, pipeConns);
            result.Pipelining = await MeasurePipeliningAsync(
                provider, pipeConns, pool, cursor, profile, profile.PipelineDepths,
                bytes => result.DataUsedBytes += bytes, ct).ConfigureAwait(false);
        }

        Report("done", "Done.", 100, result, null);
        return result;
    }

    // ---- Phases ----------------------------------------------------------

    private async Task<BenchmarkLatency> MeasureLatencyAsync(
        UsenetProviderConfig.ConnectionDetails provider, int samples, CancellationToken ct)
    {
        var conn = await UsenetStreamingClient.CreateNewConnection(provider, ct).ConfigureAwait(false);
        try
        {
            await conn.DateAsync(ct).ConfigureAwait(false); // warm-up; excludes TLS/first-command setup
            var measured = new List<double>(samples);
            for (var i = 0; i < samples; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                await conn.DateAsync(ct).ConfigureAwait(false);
                sw.Stop();
                measured.Add(sw.Elapsed.TotalMilliseconds);
            }

            return new BenchmarkLatency
            {
                MinMs = Math.Round(measured.Min(), 1),
                AvgMs = Math.Round(measured.Average(), 1),
                Samples = measured.Count,
            };
        }
        finally
        {
            SafeDispose(conn);
        }
    }

    private async Task<BenchmarkPipelining> MeasurePipeliningAsync(
        UsenetProviderConfig.ConnectionDetails provider, int connections, IReadOnlyList<string> pool,
        int[] cursor, BenchmarkProfile profile, int[] depths, Action<long> addData, CancellationToken ct)
    {
        var result = new BenchmarkPipelining { TestedAtConnections = connections };
        var target = TargetBytes(connections, profile);

        // Baseline: pipelining off.
        var baseline = await MeasureThroughputAsync(
            provider, connections, pool, cursor, target, profile.PerLevelMaxDuration, pipeliningDepth: 0, ct)
            .ConfigureAwait(false);
        addData(baseline.Bytes);
        result.BaselineMbPerSec = Math.Round(baseline.MbPerSec, 2);
        if (baseline.OpenedConnections == 0) return result;

        var bestMbps = baseline.MbPerSec;
        var bestDepth = 0;
        foreach (var depth in depths)
        {
            ct.ThrowIfCancellationRequested();
            var sample = await MeasureThroughputAsync(
                provider, connections, pool, cursor, target, profile.PerLevelMaxDuration, depth, ct)
                .ConfigureAwait(false);
            addData(sample.Bytes);
            result.Tested.Add(new BenchmarkPipeliningPoint { Depth = depth, MbPerSec = Math.Round(sample.MbPerSec, 2) });
            if (sample.MbPerSec > bestMbps) { bestMbps = sample.MbPerSec; bestDepth = depth; }
        }

        // Only recommend turning it on if it's a clear (>10%) win over the baseline.
        if (bestDepth > 0 && baseline.MbPerSec > 0 && bestMbps >= baseline.MbPerSec * 1.10)
        {
            result.RecommendEnabled = true;
            result.RecommendedDepth = bestDepth;
        }
        else
        {
            result.RecommendEnabled = false;
            result.RecommendedDepth = bestDepth > 0 ? bestDepth : 8;
        }

        return result;
    }

    private async Task<BenchmarkHealthPipelining> MeasureHealthPipeliningAsync(
        UsenetProviderConfig.ConnectionDetails provider,
        IReadOnlyList<string> pool,
        BenchmarkProfile profile,
        BenchmarkResult benchmarkResult,
        CancellationToken ct)
    {
        var realCount = Math.Min(profile.HealthStatSegments, pool.Count);
        var knownMissingCount = Math.Clamp(Math.Max(1, realCount / 8), 8, 32);
        var knownMissing = Enumerable.Range(0, knownMissingCount)
            .Select(i => $"nzbdav-health-benchmark-{Guid.NewGuid():N}-{i}@invalid.local")
            .ToArray();
        var knownMissingSet = knownMissing.ToHashSet(StringComparer.Ordinal);
        var ids = pool.Take(realCount).Concat(knownMissing).ToList();
        Shuffle(ids);

        var result = new BenchmarkHealthPipelining
        {
            ArticlesPerRound = ids.Count,
            Rounds = profile.HealthStatRounds,
            KnownMissingArticles = knownMissingCount,
        };
        var samples = new List<HealthDepthSample>(profile.HealthPipelineDepths.Length);

        for (var i = 0; i < profile.HealthPipelineDepths.Length; i++)
        {
            var depth = profile.HealthPipelineDepths[i];
            Report("health-pipelining", $"Testing health STAT depth {depth}…",
                ProgressPercent(20, 92, i, profile.HealthPipelineDepths.Length), benchmarkResult, null);
            var sample = await MeasureHealthDepthAsync(
                provider, ids, knownMissingSet, depth, profile.HealthStatRounds,
                profile.HealthStatRoundTimeout, ct).ConfigureAwait(false);
            samples.Add(sample);
            result.Tested.Add(sample.Point);
        }

        // The lowest depth that completed every repeated round is the correctness
        // reference. Faster depths must return exactly the same existence vector.
        var reference = samples
            .Where(x => x.Point.Reliable && x.Fingerprint is not null)
            .OrderBy(x => x.Point.Depth)
            .FirstOrDefault();
        if (reference is not null)
        {
            foreach (var sample in samples.Where(x => x.Point.Reliable && x.Fingerprint is not null))
            {
                if (sample.Fingerprint!.AsSpan().SequenceEqual(reference.Fingerprint!)) continue;
                sample.Point.Reliable = false;
                sample.Point.Failure = $"Results disagreed with reliable depth {reference.Point.Depth}.";
            }
        }

        var recommendation = HealthPipelineRecommendation.Select(result.Tested);
        result.Reliable = recommendation.HasValue;
        result.RecommendedDepth = recommendation ?? 0;
        if (!recommendation.HasValue)
            benchmarkResult.Warnings.Add(
                "No STAT pipeline depth completed every round reliably. Do not enable health pipelining for this provider.");

        return result;
    }

    private static async Task<HealthDepthSample> MeasureHealthDepthAsync(
        UsenetProviderConfig.ConnectionDetails provider,
        IReadOnlyList<string> ids,
        IReadOnlySet<string> knownMissing,
        int depth,
        int requiredRounds,
        TimeSpan roundTimeout,
        CancellationToken ct)
    {
        var point = new BenchmarkHealthPipeliningPoint
        {
            Depth = depth,
            RequiredRounds = requiredRounds,
        };
        bool[]? fingerprint = null;
        var totalElapsedMs = 0d;
        INntpClient? connection = null;
        try
        {
            connection = await TryOpenConnectionAsync(provider, ct).ConfigureAwait(false);
            if (connection is null)
            {
                point.Errors = 1;
                point.Failure = "Could not open a benchmark connection.";
                return new HealthDepthSample(point, null);
            }

            for (var round = 0; round < requiredRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                var ordered = Rotate(ids, round * Math.Max(1, ids.Count / requiredRounds));
                var byId = new Dictionary<string, bool>(ids.Count, StringComparer.Ordinal);
                using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                roundCts.CancelAfter(roundTimeout);
                var stopwatch = Stopwatch.StartNew();
                point.Requests += ordered.Count;
                try
                {
                    var responseIndex = 0;
                    await foreach (var response in connection.StatsPipelinedAsync(ordered, depth, roundCts.Token)
                                       .WithCancellation(roundCts.Token).ConfigureAwait(false))
                    {
                        if (responseIndex >= ordered.Count ||
                            !string.Equals(response.SegmentId, ordered[responseIndex], StringComparison.Ordinal))
                            throw new InvalidDataException("Provider returned an out-of-order STAT response.");
                        if (!byId.TryAdd(response.SegmentId, response.Exists))
                            throw new InvalidDataException("Provider returned a duplicate STAT response.");

                        responseIndex++;
                        point.Responses++;
                        if (response.Exists) point.Found++;
                        else point.Missing++;
                    }

                    if (responseIndex != ordered.Count)
                        throw new InvalidDataException(
                            $"Provider returned {responseIndex} of {ordered.Count} STAT responses.");
                    if (knownMissing.Any(id => byId.GetValueOrDefault(id)))
                        throw new InvalidDataException("Provider reported a synthetic missing article as present.");

                    var roundFingerprint = ids.Select(id => byId[id]).ToArray();
                    if (fingerprint is null)
                        fingerprint = roundFingerprint;
                    else if (!roundFingerprint.AsSpan().SequenceEqual(fingerprint))
                        throw new InvalidDataException("Repeated STAT rounds returned inconsistent results.");

                    point.CompletedRounds++;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && roundCts.IsCancellationRequested)
                {
                    point.Timeouts++;
                    point.Failure = $"Round exceeded {roundTimeout.TotalSeconds:0.#} seconds.";
                    break;
                }
                catch (Exception e) when (!e.IsCancellationException())
                {
                    point.Errors++;
                    point.Failure = e.Message;
                    Log.Debug(e, "Health STAT benchmark failed for {Provider} at depth {Depth}.",
                        provider.Host, depth);
                    break;
                }
                finally
                {
                    stopwatch.Stop();
                    totalElapsedMs += stopwatch.Elapsed.TotalMilliseconds;
                }
            }
        }
        finally
        {
            if (connection is not null) SafeDispose(connection);
        }

        point.AverageMs = point.CompletedRounds == 0
            ? Math.Round(totalElapsedMs, 1)
            : Math.Round(totalElapsedMs / point.CompletedRounds, 1);
        point.StatsPerSecond = totalElapsedMs <= 0
            ? 0
            : Math.Round(point.Responses * 1000d / totalElapsedMs, 1);
        point.Reliable = point.CompletedRounds == requiredRounds
                         && point.Timeouts == 0
                         && point.Errors == 0
                         && fingerprint is not null;
        return new HealthDepthSample(point, fingerprint);
    }

    private sealed record HealthDepthSample(BenchmarkHealthPipeliningPoint Point, bool[]? Fingerprint);

    private async Task<BenchmarkStartup> MeasureStartupAsync(
        UsenetProviderConfig.ConnectionDetails provider, int configuredMaxConnections, IReadOnlyList<string> pool,
        int[] cursor, BenchmarkProfile profile, Action<long> addData, CancellationToken ct)
    {
        var segmentCount = Math.Min(pool.Count, profile.StartupSegments);
        var ids = NextBatch(pool, cursor, segmentCount);
        var connectionCount = Math.Clamp(configuredMaxConnections, 1, Math.Min(segmentCount, HardConnectionCeiling));

        var baseline = await MeasureNonPipelinedStartupAsync(provider, ids, connectionCount, ct).ConfigureAwait(false);
        addData(baseline.Bytes);

        var result = new BenchmarkStartup
        {
            Segments = ids.Count,
            NonPipelinedConnections = baseline.OpenedConnections,
            NonPipelinedFirstMs = Math.Round(baseline.FirstMs, 1),
            NonPipelinedReadyMs = Math.Round(baseline.ReadyMs, 1),
        };

        var bestObservedDepth = 0;
        var bestObservedReadyMs = baseline.ReadyMs;
        var recommendedDepth = 0;
        var recommendedReadyMs = double.MaxValue;
        foreach (var depth in profile.PipelineDepths)
        {
            ct.ThrowIfCancellationRequested();
            var sample = await MeasurePipelinedStartupAsync(provider, ids, depth, ct).ConfigureAwait(false);
            addData(sample.Bytes);
            result.Pipelined.Add(new BenchmarkStartupPipeliningPoint
            {
                Depth = depth,
                FirstMs = Math.Round(sample.FirstMs, 1),
                ReadyMs = Math.Round(sample.ReadyMs, 1),
            });

            if (sample.ReadyMs > 0 && sample.ReadyMs < bestObservedReadyMs)
            {
                bestObservedReadyMs = sample.ReadyMs;
                bestObservedDepth = depth;
            }

            var improvesBuffer = baseline.ReadyMs > 0
                                 && sample.ReadyMs > 0
                                 && sample.ReadyMs <= baseline.ReadyMs * 0.90;
            var preservesFirstSegment = baseline.FirstMs > 0
                                        && sample.FirstMs > 0
                                        && sample.FirstMs <= baseline.FirstMs * 1.05;
            if (improvesBuffer && preservesFirstSegment && sample.ReadyMs < recommendedReadyMs)
            {
                recommendedReadyMs = sample.ReadyMs;
                recommendedDepth = depth;
            }
        }

        // Playback startup cares about the first segment most. Only recommend
        // playback pipelining when a tested depth improves buffer readiness and
        // does not materially delay first-segment readiness. Do not reject a
        // good depth just because a more aggressive depth had a faster buffer
        // but a worse first segment.
        result.RecommendPlaybackPipelining = recommendedDepth > 0;
        result.RecommendedDepth = recommendedDepth > 0 ? recommendedDepth : bestObservedDepth > 0 ? bestObservedDepth : 8;
        return result;
    }

    private readonly record struct StartupSample(double FirstMs, double ReadyMs, long Bytes, int OpenedConnections);

    private async Task<StartupSample> MeasureNonPipelinedStartupAsync(
        UsenetProviderConfig.ConnectionDetails provider, IReadOnlyList<string> ids, int requestedConnections,
        CancellationToken ct)
    {
        if (ids.Count == 0) return new StartupSample(0, 0, 0, 0);

        var workerCount = Math.Min(requestedConnections, ids.Count);
        var openedConnections = new StrongBox<int>(0);
        var firstReady = new TaskCompletionSource<(bool Found, double Ms)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, workerCount)
            .Select(workerIndex => RunNonPipelinedStartupWorkerAsync(
                provider, ids, workerIndex, workerCount, openedConnections, firstReady, sw, ct))
            .ToArray();

        var bytes = (await Task.WhenAll(workers).ConfigureAwait(false)).Sum();
        sw.Stop();
        firstReady.TrySetResult((false, 0));
        var first = await firstReady.Task.ConfigureAwait(false);
        return new StartupSample(first.Found ? first.Ms : 0, sw.Elapsed.TotalMilliseconds, bytes,
            openedConnections.Value);
    }

    private async Task<long> RunNonPipelinedStartupWorkerAsync(
        UsenetProviderConfig.ConnectionDetails provider,
        IReadOnlyList<string> ids,
        int workerIndex,
        int workerCount,
        StrongBox<int> openedConnections,
        TaskCompletionSource<(bool Found, double Ms)> firstReady,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        INntpClient? connection = null;
        var bytes = 0L;
        try
        {
            connection = await TryOpenConnectionAsync(provider, ct).ConfigureAwait(false);
            if (connection == null)
            {
                if (workerIndex == 0) firstReady.TrySetResult((false, 0));
                return 0;
            }

            Interlocked.Increment(ref openedConnections.Value);
            for (var index = workerIndex; index < ids.Count; index += workerCount)
            {
                var fetched = await FetchStartupBodyAsync(connection, ids[index], ct).ConfigureAwait(false);
                bytes += fetched.Bytes;
                if (index == 0)
                    firstReady.TrySetResult((fetched.Found, fetched.Found ? stopwatch.Elapsed.TotalMilliseconds : 0));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (workerIndex == 0) firstReady.TrySetResult((false, 0));
            Log.Debug(e, "Non-pipelined startup benchmark worker stopped early.");
        }
        finally
        {
            if (connection != null) SafeDispose(connection);
        }

        return bytes;
    }

    private async Task<StartupSample> MeasurePipelinedStartupAsync(
        UsenetProviderConfig.ConnectionDetails provider, IReadOnlyList<string> ids, int depth, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var conn = await TryOpenConnectionAsync(provider, ct).ConfigureAwait(false);
        if (conn == null) return new StartupSample(0, 0, 0, 0);

        try
        {
            var bytes = 0L;
            double? firstMs = null;
            var index = 0;
            await foreach (var result in conn.DecodedBodiesPipelinedAsync(ids, depth, ct)
                               .WithCancellation(ct).ConfigureAwait(false))
            {
                if (result is { Found: true, Stream: not null })
                {
                    bytes += await DrainCountAsync(result.Stream, ct).ConfigureAwait(false);
                    if (index == 0) firstMs = sw.Elapsed.TotalMilliseconds;
                }
                else if (index == 0)
                {
                    firstMs = 0;
                }

                index++;
            }
            sw.Stop();

            return new StartupSample(firstMs ?? 0, sw.Elapsed.TotalMilliseconds, bytes, 1);
        }
        finally
        {
            SafeDispose(conn);
        }
    }

    private readonly record struct StartupBodyResult(bool Found, long Bytes);

    private static async Task<StartupBodyResult> FetchStartupBodyAsync(
        INntpClient conn, string id, CancellationToken ct)
    {
        try
        {
            var response = await conn.DecodedBodyAsync(id, ct).ConfigureAwait(false);
            var bytes = await DrainCountAsync(response.Stream, ct).ConfigureAwait(false);
            return new StartupBodyResult(true, bytes);
        }
        catch (UsenetArticleNotFoundException)
        {
            return new StartupBodyResult(false, 0);
        }
    }

    private static async Task<long> DrainCountAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var total = 0L;
        await using (stream)
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                total += n;
        }

        return total;
    }

    // ---- Throughput core -------------------------------------------------

    private readonly record struct ThroughputSample(double MbPerSec, long Bytes, int OpenedConnections);

    private async Task<ThroughputSample> MeasureThroughputAsync(
        UsenetProviderConfig.ConnectionDetails provider, int requestedConnections, IReadOnlyList<string> pool,
        int[] cursor, long targetBytes, TimeSpan maxDuration, int pipeliningDepth, CancellationToken ct)
    {
        var connections = new List<INntpClient>(requestedConnections);
        try
        {
            for (var i = 0; i < requestedConnections; i++)
            {
                ct.ThrowIfCancellationRequested();
                var conn = await TryOpenConnectionAsync(provider, ct).ConfigureAwait(false);
                if (conn == null) break; // hit the provider's ceiling
                connections.Add(conn);
            }

            if (connections.Count == 0)
                return new ThroughputSample(0, 0, 0);

            var counter = new StrongBox<long>(0);
            using var levelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            levelCts.CancelAfter(maxDuration);
            var token = levelCts.Token;

            var sw = Stopwatch.StartNew();
            var workers = connections
                .Select(conn => Task.Run(() =>
                    DownloadWorkerAsync(conn, pool, cursor, targetBytes, counter, pipeliningDepth, token), token))
                .ToList();
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the measurement window elapsed.
            }
            sw.Stop();

            var seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            var megabytes = counter.Value / 1_000_000.0;
            return new ThroughputSample(megabytes / seconds, counter.Value, connections.Count);
        }
        finally
        {
            foreach (var conn in connections) SafeDispose(conn);
        }
    }

    private static async Task DownloadWorkerAsync(
        INntpClient conn, IReadOnlyList<string> pool, int[] cursor, long targetBytes,
        StrongBox<long> counter, int depth, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            if (depth <= 1)
            {
                while (!ct.IsCancellationRequested && Interlocked.Read(ref counter.Value) < targetBytes)
                {
                    var id = NextId(pool, cursor);
                    try
                    {
                        var response = await conn.DecodedBodyAsync(id, ct).ConfigureAwait(false);
                        await DrainAsync(response.Stream, buffer, counter, ct).ConfigureAwait(false);
                    }
                    catch (UsenetArticleNotFoundException)
                    {
                        // Expired/removed article — just move on to the next id.
                    }
                }
            }
            else
            {
                while (!ct.IsCancellationRequested && Interlocked.Read(ref counter.Value) < targetBytes)
                {
                    var batch = NextBatch(pool, cursor, depth * 4);
                    await foreach (var r in conn.DecodedBodiesPipelinedAsync(batch, depth, ct)
                                       .WithCancellation(ct).ConfigureAwait(false))
                    {
                        if (r is { Found: true, Stream: not null })
                            await DrainAsync(r.Stream, buffer, counter, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested || Interlocked.Read(ref counter.Value) >= targetBytes)
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Window elapsed mid-fetch — fine.
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            // A dead/poisoned connection just drops out for the rest of this
            // window; the remaining workers carry the measurement.
            Log.Debug(e, "Benchmark download worker stopped early.");
        }
    }

    private static async Task DrainAsync(Stream stream, byte[] buffer, StrongBox<long> counter, CancellationToken ct)
    {
        await using (stream)
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                Interlocked.Add(ref counter.Value, n);
        }
    }

    // ---- Helpers ---------------------------------------------------------

    private static async Task<INntpClient?> TryOpenConnectionAsync(
        UsenetProviderConfig.ConnectionDetails provider, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await UsenetStreamingClient.CreateNewConnection(provider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // CouldNotConnect / CouldNotLogin here almost always means we've
                // hit the provider's simultaneous-connection limit (502). Retry
                // once to rule out a transient blip, then give up gracefully.
                if (attempt == 1) Log.Debug(e, "Benchmark could not open an additional connection.");
            }
        }

        return null;
    }

    private static List<int> BuildLevels(int configuredMaxConnections, BenchmarkProfile profile)
    {
        // Probe a few steps above the configured max to discover the real sweet
        // spot, but never beyond a safe hard ceiling.
        var ceiling = Math.Clamp(
            Math.Max(configuredMaxConnections + 10, configuredMaxConnections * 2),
            8, HardConnectionCeiling);

        var levels = new SortedSet<int>(
            profile.SweepLevels.Where(l => l > 0 && l <= ceiling));

        AddLevel(configuredMaxConnections / 4);
        AddLevel(configuredMaxConnections / 2);
        AddLevel((int)Math.Round(configuredMaxConnections * 0.75));
        AddLevel(configuredMaxConnections);
        AddLevel(configuredMaxConnections + 10);
        AddLevel(configuredMaxConnections * 2);

        return levels.ToList();

        void AddLevel(int value)
        {
            if (value > 0) levels.Add(Math.Clamp(value, 1, ceiling));
        }
    }

    private static int? DetectKnee(List<BenchmarkSweepPoint> sweep, int? providerCap, List<string> warnings)
    {
        if (sweep.Count == 0) return null;

        var ordered = sweep.OrderBy(p => p.Connections).ToList();
        var best = ordered.Max(p => p.MbPerSec);
        if (best <= 0) return ordered[0].Connections;

        // Smallest connection count that reaches ~92% of peak throughput.
        var knee = ordered.First(p => p.MbPerSec >= 0.92 * best).Connections;
        if (providerCap.HasValue) knee = Math.Min(knee, providerCap.Value);

        // If we were still climbing at the top of the sweep, say so.
        var peak = ordered[^1];
        if (peak.MbPerSec >= best - 1e-9 && ordered.Count >= 2)
        {
            var prev = ordered[^2];
            if (prev.MbPerSec > 0 && (peak.MbPerSec - prev.MbPerSec) / prev.MbPerSec > 0.08)
                warnings.Add("Speed was still climbing at the highest level tested — a faster line or even more connections may help.");
        }

        return Math.Max(1, knee);
    }

    // Scale how much data a level transfers with its connection count so even
    // high counts move enough bytes per connection to measure meaningfully,
    // clamped between the profile's base and 3× that base.
    private static long TargetBytes(int connections, BenchmarkProfile profile) =>
        Math.Clamp((long)connections * ArticleBytesEstimate * 2, profile.PerLevelBytes, profile.PerLevelBytes * 3);

    // A wider depth spread for pipelining-only runs, where the depth is the whole point.
    private static int[] FocusedPipelineDepths(BenchmarkIntensity intensity) =>
        intensity == BenchmarkIntensity.Thorough ? [4, 8, 16, 32] : [4, 8, 16];

    private static string NextId(IReadOnlyList<string> pool, int[] cursor)
    {
        var i = Interlocked.Increment(ref cursor[0]) - 1;
        return pool[((i % pool.Count) + pool.Count) % pool.Count];
    }

    private static List<string> NextBatch(IReadOnlyList<string> pool, int[] cursor, int count)
    {
        var batch = new List<string>(count);
        for (var i = 0; i < count; i++) batch.Add(NextId(pool, cursor));
        return batch;
    }

    private static List<string> Rotate(IReadOnlyList<string> items, int offset)
    {
        if (items.Count == 0) return [];
        offset = ((offset % items.Count) + items.Count) % items.Count;
        var rotated = new List<string>(items.Count);
        for (var i = 0; i < items.Count; i++) rotated.Add(items[(i + offset) % items.Count]);
        return rotated;
    }

    private static void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static int ProgressPercent(int start, int end, int step, int totalSteps) =>
        start + (int)((end - start) * (double)step / Math.Max(1, totalSteps));

    private void Report(string phase, string status, int percent, BenchmarkResult result, int? currentConnections)
    {
        var update = new BenchmarkProgressUpdate
        {
            Phase = phase,
            Status = status,
            Percent = percent,
            CurrentConnections = currentConnections,
            DataUsedBytes = result.DataUsedBytes,
            Sweep = result.Sweep.Select(p => new BenchmarkSweepPoint { Connections = p.Connections, MbPerSec = p.MbPerSec }).ToList(),
        };
        // Fire-and-forget: progress is best-effort and must not block the run.
        _ = websocketManager.SendMessage(WebsocketTopic.BenchmarkProgress, JsonSerializer.Serialize(update, JsonOptions));
    }

    private static void SafeDispose(INntpClient conn)
    {
        try { conn.Dispose(); }
        catch (Exception e) { Log.Debug(e, "Failed to dispose benchmark connection."); }
    }
}
