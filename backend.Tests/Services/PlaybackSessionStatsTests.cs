using System.Text.Json;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class PlaybackSessionStatsTests
{
    [Fact]
    public void Fold_SumsCountersAndKeepsExtremesAcrossRequests()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;

        stats.Fold(sessionId, CreateDelta(
            firstByteMs: 1200,
            maxOffset: 5_000,
            cacheHits: 3,
            requestStartedAt: start));
        // Stalls arrive as they happen rather than with the request that saw them.
        stats.RecordStall(sessionId, isUpstream: true, 900);
        stats.RecordStall(sessionId, isUpstream: true, 500);
        stats.Fold(sessionId, CreateDelta(
            firstByteMs: 40,
            maxOffset: 900,
            cacheHits: 4,
            requestStartedAt: start.AddSeconds(30)));
        stats.RecordStall(sessionId, isUpstream: true, 4_000);

        var totals = stats.Take(sessionId);

        Assert.NotNull(totals);
        Assert.Equal(2, totals.RequestCount);
        // Startup latency belongs to the request that started first. The 40 ms
        // is a later seek off an already-warm stream, not what the viewer waited.
        Assert.Equal(1200, totals.FirstByteMs);
        Assert.Equal(5_000, totals.MaxOffset);
        Assert.Equal(3, totals.UpstreamStalls);
        Assert.Equal(4_000, totals.MaxUpstreamStallMs);
        Assert.Equal(5_400, totals.TotalUpstreamStallMs);
        Assert.Equal(7, totals.CacheHits);
    }

    [Fact]
    public void Fold_TimeWeightsReadAheadAndKeepsTheLowestQualifiedMinimum()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();

        stats.Fold(sessionId, CreateDelta(
            readAheadByteMilliseconds: 40_000,
            readAheadMeasuredMilliseconds: 1_000,
            minimumReadAheadBytes: 10));
        stats.Fold(sessionId, CreateDelta(
            readAheadByteMilliseconds: 20_000,
            readAheadMeasuredMilliseconds: 1_000,
            minimumReadAheadBytes: 5));

        var totals = stats.Take(sessionId);

        Assert.NotNull(totals);
        Assert.Equal(30, totals.AverageReadAheadBytes);
        Assert.Equal(5, totals.MinimumReadAheadBytes);
    }

    [Fact]
    public void RecordStall_CountsBeforeAnyRequestHasCompleted()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();

        // A sequential stream is one long request: the live view must see this
        // without waiting for the request to end.
        stats.RecordStall(sessionId, isUpstream: true, 2_500);
        stats.RecordStall(sessionId, isUpstream: false, 700);

        var live = stats.Peek(sessionId);

        Assert.NotNull(live);
        Assert.Equal(0, live.RequestCount);
        Assert.Equal(1, live.UpstreamStalls);
        Assert.Equal(2_500, live.TotalUpstreamStallMs);
        Assert.Equal(1, live.DownstreamStalls);
        Assert.Equal(700, live.TotalDownstreamStallMs);
    }

    [Fact]
    public void RecordWait_CountsAWaitReportedWhileRunningExactlyOnce()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();

        // The same wait, reported at one second, at two, and again when it ended
        // after four. One wait, four seconds — not three waits and seven seconds.
        stats.RecordWait(sessionId, isUpstream: true, deltaMs: 1_000, totalElapsedMs: 1_000, isNewWait: true);
        stats.RecordWait(sessionId, isUpstream: true, deltaMs: 1_000, totalElapsedMs: 2_000, isNewWait: false);
        stats.RecordWait(sessionId, isUpstream: true, deltaMs: 2_000, totalElapsedMs: 4_000, isNewWait: false);

        var live = stats.Peek(sessionId);

        Assert.NotNull(live);
        Assert.Equal(1, live.UpstreamStalls);
        Assert.Equal(4_000, live.TotalUpstreamStallMs);
        Assert.Equal(4_000, live.MaxUpstreamStallMs);
    }

    [Fact]
    public void BeginAndEndWait_TrackOnlyAWaitThatIsStillInProgress()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();

        stats.BeginWait(sessionId, isUpstream: true);
        stats.RecordWait(
            sessionId,
            isUpstream: true,
            deltaMs: 1_000,
            totalElapsedMs: 1_000,
            isNewWait: true);

        Assert.Equal(1, stats.Peek(sessionId)!.ActiveUpstreamWaits);

        stats.EndWait(sessionId, isUpstream: true);

        var ended = stats.Peek(sessionId);
        Assert.NotNull(ended);
        Assert.Equal(0, ended.ActiveUpstreamWaits);
        Assert.Equal(1, ended.UpstreamStalls);
    }

    [Fact]
    public void ConcurrentUpstreamWaits_CountWallClockOnce()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();

        stats.BeginWait(sessionId, isUpstream: true, elapsedMs: 1_000);
        stats.BeginWait(sessionId, isUpstream: true, elapsedMs: 1_000);

        var active = stats.Peek(sessionId)!;
        Assert.Equal(2, active.ActiveUpstreamWaits);
        Assert.InRange(active.UpstreamWaitWallMs, 900, 1_500);

        stats.EndWait(sessionId, isUpstream: true);
        Assert.Equal(1, stats.Peek(sessionId)!.ActiveUpstreamWaits);
        stats.EndWait(sessionId, isUpstream: true);

        var ended = stats.Peek(sessionId)!;
        Assert.Equal(0, ended.ActiveUpstreamWaits);
        Assert.InRange(ended.UpstreamWaitWallMs, 900, 1_500);
        Assert.InRange(ended.MaxUpstreamWaitWallMs, 900, 1_500);
        Assert.Single(ended.UpstreamWaitWindows);
    }

    [Fact]
    public void Diagnostics_CarryZeroFillsAndBodyStallsOntoTheSession()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        var diagnostics = new PlaybackRequestDiagnostics(
            sessionId,
            "/media/test.mkv",
            "test.mkv",
            requestedRange: null,
            sessionStats: stats);

        diagnostics.RecordZeroFill("segment-1", 750_000);
        diagnostics.RecordBodyStallRecovery(
            "provider-1",
            "primary.example",
            "segment-2",
            transferredBytes: 400_000,
            attempt: 1);

        var live = stats.Peek(sessionId);
        Assert.NotNull(live);
        Assert.Equal(1, live.ZeroFilledSegments);
        Assert.Equal(750_000, live.ZeroFilledBytes);
        Assert.Equal(1, live.BodyStallRecoveries);

        diagnostics.Complete("completed", "primary.example:2", bytesFetched: 750_000, failoverSaves: 0);

        var totals = stats.Take(sessionId);

        Assert.NotNull(totals);
        // Without these the play is indistinguishable from one that served every
        // byte it was asked for.
        Assert.Equal(1, totals.ZeroFilledSegments);
        Assert.Equal(750_000, totals.ZeroFilledBytes);
        Assert.Equal(1, totals.BodyStallRecoveries);
    }

    [Fact]
    public void Fold_MergesBackupProvidersById()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();

        stats.Fold(sessionId, CreateDelta(backups:
        [
            new PlaybackBackupProviderStat("backup-1", "backup.example", 2, 1, 1, 0, 0),
        ]));
        stats.Fold(sessionId, CreateDelta(backups:
        [
            new PlaybackBackupProviderStat("backup-1", "backup.example", 3, 2, 0, 1, 0),
            new PlaybackBackupProviderStat("backup-2", "other.example", 1, 0, 0, 0, 1),
        ]));

        var totals = stats.Take(sessionId);

        Assert.NotNull(totals);
        Assert.Equal(2, totals.BackupProviders.Count);
        var merged = totals.BackupProviders.Single(x => x.ProviderId == "backup-1");
        Assert.Equal("backup.example", merged.Host);
        Assert.Equal(5, merged.Attempts);
        Assert.Equal(3, merged.Rescued);
        Assert.Equal(1, merged.Missing);
        Assert.Equal(1, merged.Timeouts);
        Assert.Equal(0, merged.Errors);
    }

    [Fact]
    public void Take_RemovesTheSessionSoItIsPersistedOnce()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        stats.Fold(sessionId, CreateDelta(cacheHits: 1));

        Assert.NotNull(stats.Take(sessionId));
        Assert.Null(stats.Take(sessionId));
        Assert.Equal(0, stats.Count);
    }

    [Fact]
    public void DropStale_KeepsFreshSessions()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        stats.Fold(sessionId, CreateDelta());

        Assert.Equal(0, stats.DropStale(TimeSpan.FromMinutes(10)));
        Assert.Equal(1, stats.DropStale(TimeSpan.Zero));
        Assert.Equal(0, stats.Count);
    }

    [Fact]
    public void DropStale_NeverDropsARegisteredActiveSession()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        stats.Fold(sessionId, CreateDelta());

        Assert.Equal(
            0,
            stats.DropStale(TimeSpan.Zero, new HashSet<Guid> { sessionId }));
        Assert.NotNull(stats.Peek(sessionId));
    }

    [Fact]
    public void Diagnostics_FoldTheirTotalsIntoTheSessionOnCompletion()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        var diagnostics = new PlaybackRequestDiagnostics(
            sessionId,
            "/media/test.mkv",
            "test.mkv",
            requestedRange: null,
            stallThreshold: TimeSpan.FromMilliseconds(1),
            sessionStats: stats);

        diagnostics.RecordBackupAttempt("backup-1", "backup.example", "segment", "primary:timeout");
        diagnostics.RecordBackupOutcome("backup-1", "backup.example", "segment", "rescued", 42);
        diagnostics.RecordFallbackRescue("backup.example", "segment", "primary:timeout", 42);
        diagnostics.RecordCacheMiss();
        diagnostics.RecordProviderPoolWait("primary.example", 18, "acquired", 10, 9, 1, 2);
        diagnostics.RecordTransfer(64, 1_024, upstreamReadMs: 900, downstreamWriteMs: 0);

        diagnostics.Complete("completed", "primary.example:1", bytesFetched: 64, failoverSaves: 1);
        // A second Complete must not double-count.
        diagnostics.Complete("completed", "primary.example:1", bytesFetched: 64, failoverSaves: 1);

        var totals = stats.Take(sessionId);

        Assert.NotNull(totals);
        Assert.Equal(1, totals.RequestCount);
        Assert.Equal(1_024, totals.MaxOffset);
        Assert.Equal(1, totals.UpstreamStalls);
        Assert.True(totals.MaxUpstreamStallMs >= 900);
        Assert.Equal(1, totals.FallbackRescues);
        Assert.Equal(1, totals.CacheMisses);
        Assert.Equal(1, totals.ProviderPoolWaits);
        Assert.Equal(18, totals.MaxProviderPoolWaitMs);
        Assert.Equal(0, totals.ZeroFilledSegments);
        Assert.Null(totals.ErrorNote);
        var backup = Assert.Single(totals.BackupProviders);
        Assert.Equal("backup-1", backup.ProviderId);
        Assert.Equal(1, backup.Attempts);
        Assert.Equal(1, backup.Rescued);
    }

    [Fact]
    public void Diagnostics_RecordTheTerminalErrorOnTheSession()
    {
        var stats = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        var diagnostics = new PlaybackRequestDiagnostics(
            sessionId,
            "/media/test.mkv",
            "test.mkv",
            requestedRange: null,
            sessionStats: stats);

        diagnostics.Complete(
            "error",
            "none",
            bytesFetched: 0,
            failoverSaves: 0,
            new InvalidOperationException("segment 12 unavailable"));

        var totals = stats.Take(sessionId);

        Assert.NotNull(totals);
        Assert.Equal("error: segment 12 unavailable", totals.ErrorNote);
    }

    [Fact]
    public void ProviderStatsJson_MergesUsageAndBackupActivityByProviderId()
    {
        var totals = new PlaybackSessionStats();
        var sessionId = Guid.NewGuid();
        totals.Fold(sessionId, CreateDelta(backups:
        [
            new PlaybackBackupProviderStat("backup-1", "backup.example", 4, 1, 2, 1, 0),
        ]));

        var json = PlaybackSessionRecorder.BuildProviderStatsJson(
            segmentsByProvider: new Dictionary<string, long>
            {
                ["primary-1"] = 120,
                ["backup-1"] = 3,
            },
            bytesByProvider: new Dictionary<string, long>
            {
                ["primary-1"] = 90_000,
                ["backup-1"] = 2_000,
            },
            totals.Take(sessionId));

        Assert.NotNull(json);
        var stats = JsonSerializer.Deserialize<List<PlaybackProviderStat>>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.NotNull(stats);
        Assert.Equal(2, stats.Count);
        // Ordered by segments served, so the primary leads.
        Assert.Equal("primary-1", stats[0].ProviderId);
        Assert.Equal(120, stats[0].Segments);
        Assert.Equal(90_000, stats[0].Bytes);
        Assert.False(stats[0].IsBackup);

        var backup = stats[1];
        Assert.Equal("backup-1", backup.ProviderId);
        Assert.Equal(3, backup.Segments);
        Assert.Equal(2_000, backup.Bytes);
        Assert.Equal(4, backup.Attempts);
        Assert.Equal(1, backup.Rescued);
        Assert.Equal(2, backup.Missing);
        Assert.Equal(1, backup.Timeouts);
        Assert.True(backup.IsBackup);
    }

    [Fact]
    public void ProviderStatsJson_IsNullWhenNothingWasRecorded()
    {
        var json = PlaybackSessionRecorder.BuildProviderStatsJson(
            new Dictionary<string, long>(),
            new Dictionary<string, long>(),
            totals: null);

        Assert.Null(json);
    }

    private static PlaybackRequestDelta CreateDelta(
        long? firstByteMs = null,
        long maxOffset = 0,
        int cacheHits = 0,
        IReadOnlyList<PlaybackBackupProviderStat>? backups = null,
        DateTimeOffset? requestStartedAt = null,
        double readAheadByteMilliseconds = 0,
        double readAheadMeasuredMilliseconds = 0,
        long? minimumReadAheadBytes = null) =>
        new(
            requestStartedAt ?? DateTimeOffset.UtcNow,
            firstByteMs,
            maxOffset,
            FallbackRescues: 0,
            ProviderRotations: 0,
            FallbackBudgetExhaustions: 0,
            cacheHits,
            CacheMisses: 0,
            ConnectionPermitWaits: 0,
            MaxConnectionPermitWaitMs: 0,
            ProviderPoolWaits: 0,
            MaxProviderPoolWaitMs: 0,
            ZeroFilledSegments: 0,
            ZeroFilledBytes: 0,
            BodyStallRecoveries: 0,
            readAheadByteMilliseconds,
            readAheadMeasuredMilliseconds,
            minimumReadAheadBytes,
            backups ?? [],
            ErrorNote: null);
}
