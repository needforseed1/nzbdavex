using System.Text.Json;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services;

public class PlaybackSessionRecorderTests
{
    [Fact]
    public void Record_ConsumesProviderAndDiagnosticStateAfterQueueingHistory()
    {
        var id = Guid.NewGuid();
        var usage = new ProviderUsageTracker();
        using (usage.BeginScope(id))
        {
            usage.RecordSuccess("primary-1");
            usage.RecordFailoverSave();
            using (usage.BeginByteCapture())
                usage.RecordBytes("primary-1", 1_200);
        }

        var totals = new PlaybackSessionStats();
        totals.RecordStall(id, isUpstream: true, elapsedMs: 1_500);
        using var writer = new MetricsWriter();
        var recorder = new PlaybackSessionRecorder(usage, totals, writer);

        recorder.Record(new ActiveReadSessionSnapshot(
            id,
            "/content/movie",
            "movie.mkv",
            2_000,
            "nuvio",
            "10.0.0.5",
            null,
            null,
            ReadSession.EndReasonCode.Completed,
            DateTimeOffset.UtcNow.AddSeconds(-5),
            DateTimeOffset.UtcNow,
            BytesRead: 1_500,
            CurrentOffset: 1_500,
            MaxOffset: 1_500));

        Assert.Empty(usage.Snapshot(id));
        Assert.Empty(usage.SnapshotBytes(id));
        Assert.Null(totals.Peek(id));
        Assert.Equal(1, writer.Stats.QueuedSessions);
    }

    [Fact]
    public void BuildSession_MapsTheTerminalSnapshotAndDiagnostics()
    {
        var id = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var entry = new ActiveReadSessionSnapshot(
            id,
            "/content/movie",
            "movie.mkv",
            2_000,
            "nuvio",
            "10.0.0.5",
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReadSession.EndReasonCode.Aborted,
            started,
            started.AddSeconds(12),
            BytesRead: 1_500,
            CurrentOffset: 700,
            MaxOffset: 800);
        var totals = Totals();

        var session = PlaybackSessionRecorder.BuildSession(
            entry,
            failoverSaves: 3,
            segmentsByProvider: new Dictionary<string, long>
            {
                ["primary-1"] = 5,
                ["backup-1"] = 2,
            },
            bytesByProvider: new Dictionary<string, long>
            {
                ["primary-1"] = 1_200,
                ["backup-1"] = 300,
            },
            totals);

        Assert.Equal(id, session.Id);
        Assert.Equal(12_000, session.DurationMs);
        Assert.Equal(1_500, session.BytesServed);
        Assert.Equal(1_500, session.BytesFetched);
        Assert.Equal(3, session.FailoverSaves);
        Assert.Equal(ReadSession.EndReasonCode.Aborted, session.EndReason);
        Assert.Equal(2, session.RequestCount);
        Assert.Equal(1_200, session.FirstByteMs);
        Assert.Equal(900, session.MaxOffset);
        Assert.Equal(3, session.UpstreamStalls);
        Assert.Equal(4_000, session.MaxUpstreamStallMs);
        Assert.Equal(5_400, session.TotalUpstreamStallMs);
        Assert.Equal(1, session.HeadOfLineStalls);
        Assert.Equal(900, session.TotalHeadOfLineStallMs);
        Assert.Equal(1, session.ZeroFilledSegments);
        Assert.Equal(750_000, session.ZeroFilledBytes);
        Assert.Equal("error: source failed", session.ErrorNote);

        var providers = JsonSerializer.Deserialize<List<PlaybackProviderStat>>(
            session.ProviderStatsJson!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(providers);
        Assert.Equal(new[] { "primary-1", "backup-1" }, providers.Select(x => x.ProviderId));
        var backup = providers.Single(x => x.ProviderId == "backup-1");
        Assert.True(backup.IsBackup);
        Assert.Equal(4, backup.Attempts);
        Assert.Equal(2, backup.Rescued);
    }

    private static PlaybackSessionTotals Totals() =>
        new(
            RequestCount: 2,
            FirstByteMs: 1_200,
            MaxOffset: 900,
            UpstreamStalls: 3,
            MaxUpstreamStallMs: 4_000,
            TotalUpstreamStallMs: 5_400,
            HeadOfLineStalls: 1,
            TotalHeadOfLineStallMs: 900,
            ActiveUpstreamWaits: 0,
            DownstreamStalls: 1,
            MaxDownstreamStallMs: 700,
            TotalDownstreamStallMs: 700,
            ActiveDownstreamWaits: 0,
            FallbackRescues: 2,
            ProviderRotations: 1,
            FallbackBudgetExhaustions: 0,
            CacheHits: 10,
            CacheMisses: 2,
            ConnectionPermitWaits: 1,
            MaxConnectionPermitWaitMs: 600,
            ProviderPoolWaits: 2,
            MaxProviderPoolWaitMs: 800,
            ZeroFilledSegments: 1,
            ZeroFilledBytes: 750_000,
            BodyStallRecoveries: 1,
            BackupProviders:
            [
                new PlaybackBackupProviderStat(
                    "backup-1",
                    "backup.example",
                    Attempts: 4,
                    Rescued: 2,
                    Missing: 1,
                    Timeouts: 1,
                    Errors: 0),
            ],
            ErrorNote: "error: source failed");
}
