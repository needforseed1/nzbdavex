using System.Text.Json;
using NzbWebDAV.Api.Controllers.GetPlaybackSessions;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api;

public class PlaybackHistoryTests
{
    [Fact]
    public void BuildSession_ResolvesProviderNamesFromCurrentConfig()
    {
        var session = PlaybackHistory.BuildSession(
            CreateRow(providerStatsJson: SerializeProviders(
                new PlaybackProviderStat("primary-1", 400, 800_000, 0, 0, 0, 0, 0, false),
                new PlaybackProviderStat("backup-1", 12, 40_000, 20, 12, 6, 2, 0, true))),
            Providers());

        Assert.Equal(2, session.Providers.Count);
        Assert.Equal("news.primary.test", session.Providers[0].Host);
        Assert.Equal("Primary", session.Providers[0].Nickname);
        Assert.Equal(400, session.Providers[0].Segments);
        Assert.False(session.Providers[0].IsBackup);

        var backup = session.Providers[1];
        Assert.Equal("news.backup.test", backup.Host);
        Assert.Equal(12, backup.Rescued);
        Assert.True(backup.IsBackup);
    }

    [Fact]
    public void BuildSession_FlagsSubstitutedZerosAsAnIssue()
    {
        var corrupted = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 1, zeroFilledSegments: 2, zeroFilledBytes: 1_500_000),
            Providers());
        var stalled = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 1, bodyStallRecoveries: 1),
            Providers());

        // A play that served zeros in place of articles it could not fetch is
        // the one thing on this page that is not a delay, and it read as clean.
        Assert.Contains("corrupted", corrupted.Issues);
        Assert.Contains("body-stalled", stalled.Issues);
    }

    [Fact]
    public void IsProbe_NeverHidesAPlayThatServedZeros()
    {
        // Small and unremarkable except that part of it was fabricated. Filed as
        // a library scan it would never be looked at.
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays([
            PlaybackHistory.BuildSession(
                CreateRow(requestCount: 1, bytesServed: 200_000, zeroFilledSegments: 1),
                Providers()),
        ]));

        Assert.False(play.IsProbe);
        Assert.Contains("corrupted", play.Issues);
    }

    [Fact]
    public void DescribeIssues_IgnoresWaitsTooSmallToReachTheViewer()
    {
        var minor = PlaybackHistory.BuildSession(
            CreateRow(
                requestCount: 1,
                upstreamStalls: 1,
                maxUpstreamStallMs: 1_200,
                connectionPermitWaits: 2,
                maxConnectionPermitWaitMs: 1_400),
            Providers());
        var real = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 1, upstreamStalls: 1, maxUpstreamStallMs: 18_600),
            Providers());

        Assert.Empty(minor.Issues);
        Assert.Contains("stalled", real.Issues);
    }

    [Fact]
    public void BuildSession_MarksLegacyRowsAsHavingNoDiagnostics()
    {
        var legacy = PlaybackHistory.BuildSession(CreateRow(), Providers());
        var recorded = PlaybackHistory.BuildSession(CreateRow(requestCount: 4), Providers());

        Assert.False(legacy.HasDiagnostics);
        Assert.Empty(legacy.Providers);
        Assert.True(recorded.HasDiagnostics);
    }

    [Fact]
    public void BuildSession_UnparseableProviderStatsDegradeToEmpty()
    {
        var session = PlaybackHistory.BuildSession(
            CreateRow(providerStatsJson: "{not json"),
            Providers());

        Assert.Empty(session.Providers);
    }

    [Fact]
    public void DescribeIssues_FlagsEachKindOfTrouble()
    {
        var session = PlaybackHistory.BuildSession(
            CreateRow(
                upstreamStalls: 3,
                providerRotations: 1,
                fallbackBudgetExhaustions: 1,
                providerPoolWaits: 2,
                maxProviderPoolWaitMs: 5_000,
                connectionPermitWaits: 1,
                maxConnectionPermitWaitMs: 5_000,
                endReason: ReadSession.EndReasonCode.Timeout,
                providerStatsJson: SerializeProviders(
                    new PlaybackProviderStat("backup-1", 5, 1_000, 9, 3, 1, 0, 0, true))),
            Providers());

        Assert.Contains(PlaybackHistory.Issue.Stalled, session.Issues);
        Assert.Contains(PlaybackHistory.Issue.Rescued, session.Issues);
        Assert.Contains(PlaybackHistory.Issue.BackupUsed, session.Issues);
        Assert.Contains(PlaybackHistory.Issue.Rotated, session.Issues);
        Assert.Contains(PlaybackHistory.Issue.BudgetExhausted, session.Issues);
        Assert.Contains(PlaybackHistory.Issue.PoolStarved, session.Issues);
        Assert.Contains(PlaybackHistory.Issue.PermitStarved, session.Issues);
        Assert.Contains(PlaybackHistory.Issue.TimedOut, session.Issues);
    }

    [Fact]
    public void DescribeIssues_IgnoresClientBackpressure()
    {
        // A slow write means the player stopped reading because its buffer was
        // full. That is what healthy playback looks like, so it must not be
        // reported as trouble.
        var session = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 5, downstreamStalls: 40),
            Providers());

        Assert.Empty(session.Issues);
    }

    [Fact]
    public void DescribeIssues_NeedsMoreThanOneBriefUpstreamWait()
    {
        var brief = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 5, upstreamStalls: 1, maxUpstreamStallMs: 1_100),
            Providers());
        var repeated = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 5, upstreamStalls: 3, maxUpstreamStallMs: 1_100),
            Providers());
        var long_ = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 5, upstreamStalls: 1, maxUpstreamStallMs: 6_500),
            Providers());

        Assert.Empty(brief.Issues);
        Assert.Contains(PlaybackHistory.Issue.Stalled, repeated.Issues);
        Assert.Contains(PlaybackHistory.Issue.Stalled, long_.Issues);
    }

    [Fact]
    public void DescribeIssues_IgnoresConnectionWaitsTheBufferAbsorbed()
    {
        // The Walking Dead, 81s, completed: two pool waits worst 1.4s, zero
        // upstream stalls, source running ahead of the playhead the whole way.
        // Nothing reached the viewer, so nothing should be reported.
        var absorbed = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 4, providerPoolWaits: 2, maxProviderPoolWaitMs: 1_426),
            Providers());
        Assert.DoesNotContain("pool-starved", absorbed.Issues);

        // A single wait long enough to outlast a buffer still counts.
        var long_ = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 4, providerPoolWaits: 1, maxProviderPoolWaitMs: 4_000),
            Providers());
        Assert.Contains("pool-starved", long_.Issues);

        // So does a repeated pattern of short ones.
        var repeated = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 4, providerPoolWaits: 5, maxProviderPoolWaitMs: 900),
            Providers());
        Assert.Contains("pool-starved", repeated.Issues);

        var permits = PlaybackHistory.BuildSession(
            CreateRow(requestCount: 4, connectionPermitWaits: 2, maxConnectionPermitWaitMs: 1_400),
            Providers());
        Assert.DoesNotContain("permit-starved", permits.Issues);
    }

    [Fact]
    public void DescribeIssues_CleanSessionHasNone()
    {
        var session = PlaybackHistory.BuildSession(
            CreateRow(
                requestCount: 6,
                providerStatsJson: SerializeProviders(
                    new PlaybackProviderStat("primary-1", 900, 1_000_000, 0, 0, 0, 0, 0, false))),
            Providers());

        Assert.Empty(session.Issues);
    }

    [Fact]
    public void GroupIntoPlays_JoinsSeekFragmentsAndSplitsOnLongGaps()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds();
        var sessions = new[]
        {
            // Same film, same client: two fragments a minute apart, then a
            // resume two hours later.
            Session(start, start + 60_000, path: "/content/movie", client: "vlc"),
            Session(start + 90_000, start + 300_000, path: "/content/movie", client: "vlc"),
            Session(start + 7_500_000, start + 7_800_000, path: "/content/movie", client: "vlc"),
            // A different player watching the same film is its own play.
            Session(start + 120_000, start + 240_000, path: "/content/movie", client: "infuse"),
        };

        var plays = PlaybackHistory.GroupIntoPlays(sessions);

        Assert.Equal(3, plays.Count);
        // Newest first.
        Assert.Equal(start + 7_500_000, plays[0].Sessions.Min(x => x.StartedAtMs));
        Assert.Single(plays[0].Sessions);

        var joined = plays.Single(p => p.Sessions.Count == 2);
        Assert.Equal("vlc", joined.ClientUserAgent);
        Assert.Equal(270_000, joined.WatchedMs);
        Assert.Equal(300_000, joined.SpanMs);
    }

    [Fact]
    public void GroupIntoPlays_MergesCountersAndProvidersAcrossFragments()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var first = PlaybackHistory.BuildSession(
            CreateRow(
                startedAt: start,
                endedAt: start + 60_000,
                requestCount: 2,
                upstreamStalls: 1,
                maxUpstreamStallMs: 1_200,
                firstByteMs: 900,
                bytesServed: 1_000,
                providerStatsJson: SerializeProviders(
                    new PlaybackProviderStat("primary-1", 100, 5_000, 0, 0, 0, 0, 0, false))),
            Providers());
        var second = PlaybackHistory.BuildSession(
            CreateRow(
                startedAt: start + 70_000,
                endedAt: start + 130_000,
                requestCount: 3,
                upstreamStalls: 2,
                maxUpstreamStallMs: 400,
                firstByteMs: 120,
                bytesServed: 3_000,
                endReason: ReadSession.EndReasonCode.Aborted,
                providerStatsJson: SerializeProviders(
                    new PlaybackProviderStat("primary-1", 40, 2_000, 0, 0, 0, 0, 0, false),
                    new PlaybackProviderStat("backup-1", 7, 900, 9, 7, 2, 0, 0, true))),
            Providers());

        var play = Assert.Single(PlaybackHistory.GroupIntoPlays([first, second]));

        Assert.Equal(3, play.Counters.UpstreamStalls);
        Assert.Equal(1_200, play.Counters.MaxUpstreamStallMs);
        // Startup latency is what the first session measured. The 120 ms belongs
        // to a later seek off a warm stream, and reporting it as "first byte"
        // hides exactly the slow start a viewer complains about.
        Assert.Equal(900, play.FirstByteMs);
        Assert.Equal(4_000, play.BytesServed);
        Assert.Equal(2, play.Providers.Count);
        Assert.Equal(140, play.Providers[0].Segments);
        Assert.Equal(7_000, play.Providers[0].Bytes);
        // The last fragment decides how the play ended.
        Assert.Equal("aborted", play.EndReason);
    }

    [Fact]
    public void GroupIntoPlays_UsesResolvedTitleThenStoredFileNameThenPath()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var resolved = PlaybackHistory.GroupIntoPlays(
            [Session(start, start + 1_000, path: "/content/a", fileName: "stored.mkv")],
            _ => new PlaybackContentInfo("Resolved.mkv", "Some.Release-GRP", "movies"));
        var stored = PlaybackHistory.GroupIntoPlays(
            [Session(start, start + 1_000, path: "/content/b", fileName: "stored.mkv")]);
        var fallback = PlaybackHistory.GroupIntoPlays(
            [Session(start, start + 1_000, path: "/content/c/opaque.mkv")]);

        Assert.Equal("Resolved.mkv", resolved[0].Title);
        Assert.Equal("Some.Release-GRP", resolved[0].NzbName);
        Assert.Equal("stored.mkv", stored[0].Title);
        Assert.Equal("opaque.mkv", fallback[0].Title);
    }

    [Fact]
    public void GroupIntoPlays_ReportsProgressAndRate()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 10_000,
                    bytesServed: 20_000_000,
                    fileSize: 1_000,
                    maxOffset: 250),
                Providers()),
        ]));

        Assert.Equal(25d, play.ReachedPct);
        Assert.Equal(2_000_000, play.AvgBytesPerSecond);
    }

    [Fact]
    public void GroupIntoPlays_SumsTimeWaitedButKeepsTheWorstSingleWait()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 10_000,
                    upstreamStalls: 2,
                    maxUpstreamStallMs: 3_000,
                    totalUpstreamStallMs: 5_000),
                Providers()),
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start + 11_000,
                    endedAt: start + 20_000,
                    upstreamStalls: 1,
                    maxUpstreamStallMs: 8_000,
                    totalUpstreamStallMs: 8_000),
                Providers()),
        ]));

        Assert.Equal(3, play.Counters.UpstreamStalls);
        Assert.Equal(8_000, play.Counters.MaxUpstreamStallMs);
        // Waiting is cumulative across the seek fragments of one play.
        Assert.Equal(13_000, play.Counters.TotalUpstreamStallMs);
    }

    [Fact]
    public void GroupIntoPlays_ReportsSourceRateAlongsideClientRate()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            // The client took 10 MB while the source delivered 40 MB: prefetch ran
            // four times ahead of playback, so usenet was not the constraint.
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 10_000,
                    bytesServed: 10_000_000,
                    bytesFetched: 40_000_000),
                Providers()),
        ]));

        Assert.Equal(1_000_000, play.AvgBytesPerSecond);
        Assert.Equal(4_000_000, play.SourceBytesPerSecond);
    }

    [Fact]
    public void MatchesFilter_IssuesExcludesPlainClientAborts()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var aborted = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 1_000,
                    requestCount: 1,
                    endReason: ReadSession.EndReasonCode.Aborted),
                Providers()),
        ]));
        var stalled = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 1_000,
                    requestCount: 1,
                    upstreamStalls: 4,
                    maxUpstreamStallMs: 5_000),
                Providers()),
        ]));

        Assert.True(PlaybackHistory.MatchesFilter(aborted, "all"));
        Assert.False(PlaybackHistory.MatchesFilter(aborted, "issues"));
        Assert.False(PlaybackHistory.MatchesFilter(aborted, "failed"));
        Assert.False(PlaybackHistory.MatchesFilter(aborted, "failed"));
        Assert.True(PlaybackHistory.MatchesFilter(stalled, "issues"));
        Assert.True(PlaybackHistory.MatchesFilter(stalled, null));
    }

    [Fact]
    public void MatchesFilter_IssuesExcludesSuccessfulRecoveryDiagnostics()
    {
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    requestCount: 1,
                    bytesServed: 200_000,
                    bodyStallRecoveries: 1,
                    providerRotations: 1,
                    fallbackBudgetExhaustions: 1),
                Providers()),
        ]));

        Assert.Contains("body-stalled", play.Issues);
        Assert.Contains("rotated", play.Issues);
        Assert.Contains("budget-exhausted", play.Issues);
        Assert.True(play.IsProbe);
        Assert.False(PlaybackHistory.MatchesFilter(play, "issues"));
        Assert.True(PlaybackHistory.MatchesFilter(play, "scans"));
    }

    [Fact]
    public void MatchesFilter_SeparatesLibraryScansFromViewing()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // A scanner reading a header: kilobytes, and it can be slow about it.
        var scan = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(startedAt: start, endedAt: start + 14_400, bytesServed: 330_000),
                Providers()),
        ]));
        var watched = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(startedAt: start, endedAt: start + 8_000, bytesServed: 54_000_000),
                Providers()),
        ]));

        Assert.True(scan.IsProbe);
        Assert.False(watched.IsProbe);
        Assert.True(PlaybackHistory.MatchesFilter(scan, "scans"));
        Assert.False(PlaybackHistory.MatchesFilter(scan, "plays"));
        Assert.True(PlaybackHistory.MatchesFilter(watched, "plays"));
        Assert.True(PlaybackHistory.MatchesFilter(scan, "all"));
    }

    [Fact]
    public void MatchesFilter_TinyButTroubledReadsStayVisible()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // A stream that died after 20 KB is the most interesting row on the
        // page — it must never be filed away as a scan.
        var died = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 2_000,
                    bytesServed: 20_000,
                    endReason: ReadSession.EndReasonCode.Error),
                Providers()),
        ]));
        var stalled = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 2_000,
                    bytesServed: 20_000,
                    upstreamStalls: 5,
                    maxUpstreamStallMs: 9_000),
                Providers()),
        ]));

        Assert.False(died.IsProbe);
        Assert.False(stalled.IsProbe);
        Assert.True(PlaybackHistory.MatchesFilter(died, "plays"));
        Assert.True(PlaybackHistory.MatchesFilter(stalled, "plays"));
    }

    [Fact]
    public void MatchesFilter_FailedIsTimeoutOrError()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var errored = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 1_000,
                    endReason: ReadSession.EndReasonCode.Error,
                    errorNote: "error: segment 12 unavailable"),
                Providers()),
        ]));

        Assert.True(PlaybackHistory.MatchesFilter(errored, "failed"));
        Assert.Equal("error: segment 12 unavailable", errored.ErrorNote);
    }

    private static GetPlaybackSessionsResponse.SessionDto Session(
        long startedAt,
        long endedAt,
        string path = "/content/movie",
        string client = "vlc",
        string? fileName = null) =>
        PlaybackHistory.BuildSession(
            CreateRow(
                startedAt: startedAt,
                endedAt: endedAt,
                path: path,
                clientUserAgent: client,
                fileName: fileName),
            Providers());

    private static ReadSession CreateRow(
        long? startedAt = null,
        long? endedAt = null,
        string path = "/content/movie",
        string? fileName = null,
        string clientUserAgent = "vlc",
        string clientIp = "10.0.0.5",
        int requestCount = 0,
        int upstreamStalls = 0,
        int maxUpstreamStallMs = 0,
        long totalUpstreamStallMs = 0,
        int downstreamStalls = 0,
        int maxDownstreamStallMs = 0,
        long bytesFetched = -1,
        int providerRotations = 0,
        int fallbackBudgetExhaustions = 0,
        int providerPoolWaits = 0,
        int maxProviderPoolWaitMs = 0,
        int connectionPermitWaits = 0,
        int maxConnectionPermitWaitMs = 0,
        int? firstByteMs = null,
        int zeroFilledSegments = 0,
        long zeroFilledBytes = 0,
        int bodyStallRecoveries = 0,
        long bytesServed = 0,
        long? fileSize = null,
        long maxOffset = 0,
        ReadSession.EndReasonCode endReason = ReadSession.EndReasonCode.Completed,
        string? errorNote = null,
        string? providerStatsJson = null)
    {
        var started = startedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ended = endedAt ?? started + 1_000;
        return new ReadSession
        {
            Id = Guid.NewGuid(),
            StartedAt = started,
            EndedAt = ended,
            DurationMs = (int)(ended - started),
            Path = path,
            FileName = fileName,
            ClientIp = clientIp,
            ClientUserAgent = clientUserAgent,
            BytesServed = bytesServed,
            BytesFetched = bytesFetched < 0 ? bytesServed : bytesFetched,
            FileSize = fileSize,
            MaxOffset = maxOffset,
            RequestCount = requestCount,
            FirstByteMs = firstByteMs,
            UpstreamStalls = upstreamStalls,
            MaxUpstreamStallMs = maxUpstreamStallMs,
            TotalUpstreamStallMs = totalUpstreamStallMs,
            DownstreamStalls = downstreamStalls,
            MaxDownstreamStallMs = maxDownstreamStallMs,
            ProviderRotations = providerRotations,
            FallbackBudgetExhaustions = fallbackBudgetExhaustions,
            ProviderPoolWaits = providerPoolWaits,
            MaxProviderPoolWaitMs = maxProviderPoolWaitMs,
            ConnectionPermitWaits = connectionPermitWaits,
            MaxConnectionPermitWaitMs = maxConnectionPermitWaitMs,
            EndReason = endReason,
            ErrorNote = errorNote,
            ZeroFilledSegments = zeroFilledSegments,
            ZeroFilledBytes = zeroFilledBytes,
            BodyStallRecoveries = bodyStallRecoveries,
            ProviderStatsJson = providerStatsJson,
        };
    }

    private static string SerializeProviders(params PlaybackProviderStat[] stats) =>
        JsonSerializer.Serialize(
            stats,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static Dictionary<string, UsenetProviderConfig.ConnectionDetails> Providers() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["primary-1"] = Provider("primary-1", "news.primary.test", "Primary", ProviderType.Pooled),
            ["backup-1"] = Provider("backup-1", "news.backup.test", null, ProviderType.BackupOnly),
        };

    private static UsenetProviderConfig.ConnectionDetails Provider(
        string id,
        string host,
        string? nickname,
        ProviderType type) =>
        new()
        {
            Id = id,
            Host = host,
            Nickname = nickname,
            Type = type,
            Port = 563,
            UseSsl = true,
            User = "user",
            Pass = "pass",
            MaxConnections = 10,
        };
}
