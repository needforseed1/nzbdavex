using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services;

public class PlaybackDiagnosticContextTests
{
    [Fact]
    public async Task Context_FlowsIntoBackgroundWorkAndRestoresPreviousScope()
    {
        var diagnostics = CreateDiagnostics();

        Assert.Null(PlaybackDiagnosticContext.Current);
        using (PlaybackDiagnosticContext.Begin(diagnostics))
        {
            Assert.Same(diagnostics, PlaybackDiagnosticContext.Current);
            var flowed = await Task.Run(() => PlaybackDiagnosticContext.Current);
            Assert.Same(diagnostics, flowed);
        }
        Assert.Null(PlaybackDiagnosticContext.Current);
    }

    [Fact]
    public async Task TransferPump_SeparatelyCountsSlowUpstreamAndDownstream()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        await using var source = new DelayedReadStream([1, 2, 3, 4], TimeSpan.FromMilliseconds(10));
        await using var destination = new DelayedWriteStream(TimeSpan.FromMilliseconds(10));

        await PlaybackTransferPump.CopyAsync(
            source,
            destination,
            diagnostics,
            startOffset: 5,
            endOffset: null,
            seekSource: false,
            onBytesServed: null,
            onSourceError: null,
            CancellationToken.None);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(4, snapshot.BytesServed);
        Assert.Equal(9, snapshot.CurrentOffset);
        Assert.Equal(1, snapshot.UpstreamStalls);
        Assert.Equal(1, snapshot.DownstreamStalls);
        Assert.True(snapshot.MaxUpstreamStallMs >= 1);
        Assert.True(snapshot.MaxDownstreamStallMs >= 1);
    }

    [Fact]
    public void UpstreamStalls_SeparateASlowSourceFromOneArticleBlockingTheQueue()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));

        // Nothing downloaded yet: the source could not keep up.
        diagnostics.RecordTransfer(
            64, 64, upstreamReadMs: 2_000, downstreamWriteMs: 0,
            upstreamOnset: (BufferedSegments: 0, InFlightSegments: 8));
        // Segments already downloaded and waiting behind the one the reader
        // needed: plenty of data on hand, all of it unusable.
        diagnostics.RecordTransfer(
            64, 128, upstreamReadMs: 5_000, downstreamWriteMs: 0,
            upstreamOnset: (BufferedSegments: 48, InFlightSegments: 2));

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(2, snapshot.UpstreamStalls);
        Assert.Equal(7_000, snapshot.TotalUpstreamStallMs);
        Assert.Equal(1, snapshot.HeadOfLineStalls);
        Assert.Equal(5_000, snapshot.TotalHeadOfLineStallMs);
    }

    [Fact]
    public void HeadOfLineStall_CountsOnceWhenReportedWhileStillRunning()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        var onset = (BufferedSegments: 40, InFlightSegments: 1);

        var reported = diagnostics.ReportWaitProgress(
            isUpstream: true, elapsedMs: 1_000, reportedMs: 0, offset: 0, onset);
        reported = diagnostics.ReportWaitProgress(
            isUpstream: true, elapsedMs: 3_000, reportedMs: reported, offset: 0, onset);
        diagnostics.RecordTransfer(
            64, 64, upstreamReadMs: 4_000, downstreamWriteMs: 0,
            upstreamOnset: onset, upstreamReportedMs: reported);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(1, snapshot.UpstreamStalls);
        Assert.Equal(1, snapshot.HeadOfLineStalls);
        // Four seconds of blocking, reported in three instalments.
        Assert.Equal(4_000, snapshot.TotalHeadOfLineStallMs);
        Assert.Equal(4_000, snapshot.TotalUpstreamStallMs);
    }

    [Fact]
    public void CompletionLog_BindsEveryCounterToItsOwnName()
    {
        // Serilog binds placeholders positionally. A placeholder added in the
        // wrong position prints one counter's value under another counter's
        // name, and the line still looks entirely plausible — this was shipped
        // once, reporting a 43-second downstream total as a head-of-line count.
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        diagnostics.RecordTransfer(
            10, 10, upstreamReadMs: 2_000, downstreamWriteMs: 0,
            upstreamOnset: (BufferedSegments: 0, InFlightSegments: 4));
        diagnostics.RecordTransfer(
            20, 30, upstreamReadMs: 5_000, downstreamWriteMs: 0,
            upstreamOnset: (BufferedSegments: 40, InFlightSegments: 1));
        diagnostics.RecordTransfer(30, 60, upstreamReadMs: 0, downstreamWriteMs: 4_000);
        diagnostics.RecordZeroFill("segment-1", 900);

        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            diagnostics.Complete("completed", "primary.example:3", bytesFetched: 4_096, failoverSaves: 0);
        }
        finally
        {
            Log.Logger = previous;
        }

        var end = Assert.Single(
            sink.Events,
            e => e.Properties.TryGetValue("RequestId", out var id)
                 && id.ToString().Contains(diagnostics.RequestId.ToString())
                 && e.MessageTemplate.Text.Contains("stage=request-end"));

        string Value(string name) => end.Properties[name].ToString();
        Assert.Equal("2", Value("UpstreamStalls"));
        Assert.Equal("5000", Value("MaxUpstreamStallMs"));
        Assert.Equal("7000", Value("TotalUpstreamStallMs"));
        Assert.Equal("1", Value("DownstreamStalls"));
        Assert.Equal("4000", Value("MaxDownstreamStallMs"));
        Assert.Equal("4000", Value("TotalDownstreamStallMs"));
        // Only the second upstream wait had segments ready behind the head.
        Assert.Equal("1", Value("HeadOfLineStalls"));
        Assert.Equal("5000", Value("TotalHeadOfLineStallMs"));
        Assert.Equal("1", Value("ZeroFilledSegments"));
        Assert.Equal("900", Value("ZeroFilledBytes"));
        Assert.Equal("60", Value("BytesServed"));
        Assert.Equal("4096", Value("BytesFetched"));
    }

    [Fact]
    public async Task TransferPump_AbandonsARequestWhoseClientStoppedReading()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        await using var source = new MemoryStream(new byte[256 * 1024]);
        // A client that vanished without closing its connection: the write never
        // completes and never faults, which is exactly what nothing below us
        // resolves — TCP reports no error and the proxy keeps its connection.
        await using var destination = new DelayedWriteStream(TimeSpan.FromMinutes(30));

        var stalled = await Assert.ThrowsAsync<DownstreamStalledException>(() =>
            PlaybackTransferPump.CopyAsync(
                source, destination, diagnostics,
                startOffset: 0, endOffset: null, seekSource: false,
                onBytesServed: null, onSourceError: null, CancellationToken.None,
                downstreamWriteTimeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(0, stalled.Offset);
        // Close to the budget, but timer granularity can land just under it, so
        // this asserts the wait was real rather than an exact duration.
        Assert.True(stalled.StalledMs >= 100, $"stalled for only {stalled.StalledMs} ms");
        // Recorded against the client, not the source: the segments were ready.
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(0, snapshot.UpstreamStalls);
        Assert.Equal(1, snapshot.DownstreamStalls);
    }

    [Fact]
    public async Task TransferPump_LetsASlowClientKeepGoingIndefinitely()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        await using var source = new MemoryStream(new byte[192 * 1024]);
        await using var destination = new DelayedWriteStream(TimeSpan.FromMilliseconds(50));

        // Three slow writes whose total far exceeds the budget, while no single
        // one approaches it. A viewer who pauses repeatedly, or a genuinely slow
        // link, must never be cut off — the deadline is per write, not for the
        // request. The margin is wide on purpose: a tight one fails under
        // parallel test load rather than because the behaviour regressed.
        await PlaybackTransferPump.CopyAsync(
            source, destination, diagnostics,
            startOffset: 0, endOffset: null, seekSource: false,
            onBytesServed: null, onSourceError: null, CancellationToken.None,
            downstreamWriteTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(192 * 1024, diagnostics.Snapshot().BytesServed);
    }

    [Fact]
    public async Task TransferPump_KeepsClientAbortDistinctFromClientGone()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        using var abort = new CancellationTokenSource();
        await using var source = new MemoryStream(new byte[256 * 1024]);
        await using var destination = new DelayedWriteStream(TimeSpan.FromMinutes(30));
        abort.CancelAfter(50);

        // A player that closes the connection cancels the request. That is an
        // ordinary abort and must not be reported as an abandoned client.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PlaybackTransferPump.CopyAsync(
                source, destination, diagnostics,
                startOffset: 0, endOffset: null, seekSource: false,
                onBytesServed: null, onSourceError: null, abort.Token,
                downstreamWriteTimeout: TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Snapshot_SummarizesBackupCacheAndPermitActivity()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));

        diagnostics.RecordBackupAttempt(
            "backup-id", "backup.example", "segment", "primary.example:timeout");
        diagnostics.RecordBackupOutcome(
            "backup-id", "backup.example", "segment", "rescued", 42);
        diagnostics.RecordFallbackRescue(
            "backup.example", "segment", "primary.example:timeout", 42);
        diagnostics.RecordCacheHit();
        diagnostics.RecordCacheHit();
        diagnostics.RecordCacheMiss();
        diagnostics.RecordConnectionPermitWait(12, "High", "acquired");
        diagnostics.RecordProviderPoolWait(
            "primary.example", 18, "acquired", 10, 9, 1, 2);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(1, snapshot.FallbackRescues);
        Assert.Equal(2, snapshot.CacheHits);
        Assert.Equal(1, snapshot.CacheMisses);
        Assert.Equal(1, snapshot.ConnectionPermitWaits);
        Assert.Equal(12, snapshot.MaxConnectionPermitWaitMs);
        Assert.Equal(1, snapshot.ProviderPoolWaits);
        Assert.Equal(18, snapshot.MaxProviderPoolWaitMs);
        Assert.Contains(
            "backup.example:attempts=1,rescued=1,missing=0,timeouts=0,errors=0",
            snapshot.BackupSummary);
    }

    [Fact]
    public void CompletionLogLevel_WarnsOnlyForActionablePlaybackIssues()
    {
        var clean = CreateDiagnostics().Snapshot();

        Assert.Equal(
            LogEventLevel.Information,
            PlaybackOutcomeClassifier.CompletionLogLevel(
                clean, "completed", exception: null));
        Assert.Equal(
            LogEventLevel.Information,
            PlaybackOutcomeClassifier.CompletionLogLevel(
                clean with { DownstreamStalls = 1 },
                "completed",
                exception: null));

        // Below the threshold the page calls a play degraded at. Warning about
        // these buries the waits a viewer actually sat through.
        var informationSnapshots = new[]
        {
            clean with { UpstreamStalls = 1, MaxUpstreamStallMs = 1_200 },
            clean with { ConnectionPermitWaits = 2, MaxConnectionPermitWaitMs = 1_400 },
            clean with { ProviderPoolWaits = 2, MaxProviderPoolWaitMs = 1_400 },
            // Routine failover that delivered the stream. Common on healthy
            // plays, so warning on it buries the faults below. The request-end
            // line still carries the counts.
            clean with { FallbackRescues = 1 },
            clean with { ProviderRotations = 1 },
            clean with { FallbackBudgetExhaustions = 1 },
        };
        foreach (var snapshot in informationSnapshots)
            Assert.Equal(
                LogEventLevel.Information,
                PlaybackOutcomeClassifier.CompletionLogLevel(
                    snapshot, "completed", exception: null));

        var warningSnapshots = new[]
        {
            clean with { UpstreamStalls = 3, MaxUpstreamStallMs = 1_100 },
            clean with { UpstreamStalls = 1, MaxUpstreamStallMs = 18_600 },
            clean with { ConnectionPermitWaits = 5, MaxConnectionPermitWaitMs = 1_100 },
            clean with { ProviderPoolWaits = 1, MaxProviderPoolWaitMs = 4_000 },
            // Corruption and wedged sockets warn on the first occurrence: they
            // are faults, not slowness, and one is already too many. The page
            // keeps a recovered connection neutral because the viewer saw
            // nothing; the log warns because a fault that heals leaves no other
            // trace. The two answer different questions on purpose.
            clean with { ZeroFilledSegments = 1, ZeroFilledBytes = 750_000 },
            clean with { BodyStallRecoveries = 1 },
        };
        foreach (var snapshot in warningSnapshots)
            Assert.Equal(
                LogEventLevel.Warning,
                PlaybackOutcomeClassifier.CompletionLogLevel(
                    snapshot, "completed", exception: null));

        Assert.Equal(
            LogEventLevel.Warning,
            PlaybackOutcomeClassifier.CompletionLogLevel(
                clean, "timeout", exception: null));
        Assert.Equal(
            LogEventLevel.Warning,
            PlaybackOutcomeClassifier.CompletionLogLevel(
                clean,
                "error",
                new InvalidOperationException("upstream failed")));
    }

    [Fact]
    public async Task StallLog_ReportsBufferDepthFromWhenTheWaitBegan()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        // The producer refills while the consumer is stuck, so reading the
        // counters after the fact would describe the recovery instead.
        await using var source = new DelayedReadStream(
            [1, 2, 3, 4], TimeSpan.FromMilliseconds(10),
            duringRead: () =>
            {
                for (var i = 0; i < 5; i++) diagnostics.SegmentBuffered();
            });
        await using var destination = new MemoryStream();
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        try
        {
            await PlaybackTransferPump.CopyAsync(
                source, destination, diagnostics,
                startOffset: 0, endOffset: null, seekSource: false,
                onBytesServed: null, onSourceError: null, CancellationToken.None);
        }
        finally
        {
            Log.Logger = previous;
        }

        // Tests share the global logger, so match this request rather than any
        // upstream stall that happened to be logged concurrently.
        var stall = Assert.Single(sink.Events, e => IsStallFor(e, diagnostics, "upstream-read"));
        // A 10 ms wait behind a full buffer reached nobody, so it is recorded
        // but does not warn — see the threshold test below.
        Assert.Equal(LogEventLevel.Information, stall.Level);
        Assert.Equal("0", stall.Properties["BufferedSegments"].ToString());
    }

    [Fact]
    public void UpstreamStall_WarnsOnlyOnceItIsBigEnoughToReachTheViewer()
    {
        var small = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        var large = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        try
        {
            small.RecordTransfer(64, 64, upstreamReadMs: 1_200, downstreamWriteMs: 0);
            large.RecordTransfer(64, 64, upstreamReadMs: 18_600, downstreamWriteMs: 0);
        }
        finally
        {
            Log.Logger = previous;
        }

        Assert.Equal(
            LogEventLevel.Information,
            Assert.Single(sink.Events, e => IsStallFor(e, small, "upstream-read")).Level);
        Assert.Equal(
            LogEventLevel.Warning,
            Assert.Single(sink.Events, e => IsStallFor(e, large, "upstream-read")).Level);
    }

    [Fact]
    public void PermitWait_IsPromotedToWarningWhenItsCountBecomesActionable()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        try
        {
            for (var i = 0; i < PlaybackIssueThresholds.WaitMinCount; i++)
                diagnostics.RecordConnectionPermitWait(
                    elapsedMs: 1_000,
                    priority: "High",
                    outcome: "acquired");
        }
        finally
        {
            Log.Logger = previous;
        }

        var events = sink.Events
            .Where(e => IsForRequest(e, diagnostics) && e.Properties.ContainsKey("Priority"))
            .ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(LogEventLevel.Information, events[0].Level);
        Assert.Equal("1", events[0].Properties["Waits"].ToString());
        Assert.Equal(LogEventLevel.Warning, events[1].Level);
        Assert.Equal(
            PlaybackIssueThresholds.WaitMinCount.ToString(),
            events[1].Properties["Waits"].ToString());
    }

    [Fact]
    public async Task TransferPump_CountsAWaitThatEndsInAClientAbort()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        using var abort = new CancellationTokenSource();
        await using var source = new DelayedReadStream(
            [1, 2, 3, 4], TimeSpan.FromSeconds(30), duringRead: () => abort.CancelAfter(20));
        await using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PlaybackTransferPump.CopyAsync(
                source, destination, diagnostics,
                startOffset: 0, endOffset: null, seekSource: false,
                onBytesServed: null, onSourceError: null, abort.Token));

        // The viewer waited and then gave up. Counting only waits that end in
        // delivered bytes reports exactly this case as a flawless request.
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(1, snapshot.UpstreamStalls);
        Assert.True(snapshot.MaxUpstreamStallMs >= 1);
    }

    [Fact]
    public async Task TransferPump_ReportsALongWaitWhileItIsStillRunning()
    {
        var sessionStats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        var diagnostics = new PlaybackRequestDiagnostics(
            sessionId,
            "/media/test.mkv",
            "test.mkv",
            requestedRange: null,
            stallThreshold: TimeSpan.FromMilliseconds(1),
            sessionStats: sessionStats);
        using var abort = new CancellationTokenSource();
        await using var source = new DelayedReadStream([1, 2, 3, 4], TimeSpan.FromSeconds(30));
        await using var destination = new MemoryStream();

        var copy = PlaybackTransferPump.CopyAsync(
            source, destination, diagnostics,
            startOffset: 0, endOffset: null, seekSource: false,
            onBytesServed: null, onSourceError: null, abort.Token);

        // A stream stuck on usenet must not read as healthy for as long as it is
        // stuck: the wait is reported while it runs, not when it resolves.
        var observed = false;
        for (var i = 0; i < 40 && !observed; i++)
        {
            await Task.Delay(100);
            observed = sessionStats.Peek(sessionId)?.ActiveUpstreamWaits == 1;
        }

        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);

        Assert.True(observed, "the in-progress wait was never reported");
        // Reported live and again on completion, but counted once.
        Assert.Equal(1, diagnostics.Snapshot().UpstreamStalls);
        Assert.Equal(0, sessionStats.Peek(sessionId)!.ActiveUpstreamWaits);
    }

    [Fact]
    public async Task DownstreamStall_LogsAtInformationSoItIsVisibleWithoutLookingLikeAFault()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        await using var source = new MemoryStream([1, 2, 3, 4]);
        await using var destination = new DelayedWriteStream(TimeSpan.FromMilliseconds(10));
        var sink = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        try
        {
            await PlaybackTransferPump.CopyAsync(
                source, destination, diagnostics,
                startOffset: 0, endOffset: null, seekSource: false,
                onBytesServed: null, onSourceError: null, CancellationToken.None);
        }
        finally
        {
            Log.Logger = previous;
        }

        var stall = Assert.Single(sink.Events, e => IsStallFor(e, diagnostics, "downstream-write"));
        // A player filling its buffer is normal, so this must never warn — but it
        // must clear the default level, or only the upstream stall is visible.
        Assert.Equal(LogEventLevel.Information, stall.Level);
    }

    [Fact]
    public async Task TransferPump_PropagatesADelayedDestinationFailure()
    {
        var diagnostics = CreateDiagnostics(TimeSpan.FromMilliseconds(1));
        await using var source = new MemoryStream([1, 2, 3, 4]);
        await using var destination = new FailingWriteStream();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            PlaybackTransferPump.CopyAsync(
                source, destination, diagnostics,
                startOffset: 0, endOffset: null, seekSource: false,
                onBytesServed: null, onSourceError: null, CancellationToken.None));

        Assert.Equal("destination failed", exception.Message);
    }

    private static bool IsStallFor(LogEvent e, PlaybackRequestDiagnostics diagnostics, string kind) =>
        e.Properties.TryGetValue("Kind", out var loggedKind) &&
        loggedKind.ToString().Contains(kind) &&
        IsForRequest(e, diagnostics);

    private static bool IsForRequest(LogEvent e, PlaybackRequestDiagnostics diagnostics) =>
        e.Properties.TryGetValue("RequestId", out var requestId) &&
        requestId.ToString().Contains(diagnostics.RequestId.ToString());

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    private static PlaybackRequestDiagnostics CreateDiagnostics(TimeSpan? stallThreshold = null) =>
        new(
            Guid.NewGuid(),
            "/media/test.mkv",
            "test.mkv",
            requestedRange: null,
            stallThreshold: stallThreshold);

    private sealed class DelayedReadStream(byte[] bytes, TimeSpan delay, Action? duringRead = null)
        : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            duringRead?.Invoke();
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class DelayedWriteStream(TimeSpan delay) : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new IOException("destination failed");
        }
    }
}
