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
                // Provider usage can be recorded without going through the
                // explicit fallback-attempt telemetry. Its configured role
                // must still identify it as backup.
                new PlaybackProviderStat("backup-1", 12, 40_000, 0, 0, 0, 0, 0, false))),
            Providers());

        Assert.Equal(2, session.Providers.Count);
        Assert.Equal("news.primary.test", session.Providers[0].Host);
        Assert.Equal("Primary", session.Providers[0].Nickname);
        Assert.Equal(400, session.Providers[0].Segments);
        Assert.False(session.Providers[0].IsBackup);

        var backup = session.Providers[1];
        Assert.Equal("news.backup.test", backup.Host);
        Assert.Equal(12, backup.Segments);
        Assert.True(backup.IsBackup);
        Assert.Contains(PlaybackHistory.Issue.BackupUsed, session.Issues);
    }

    [Fact]
    public void BuildSession_TreatsBackupAndStatsAsBackupWhenItServesData()
    {
        var providers = Providers();
        providers["backup-stats-1"] = Provider(
            "backup-stats-1",
            "news.backup-stats.test",
            "Backup + stats",
            ProviderType.BackupAndStats);

        var session = PlaybackHistory.BuildSession(
            CreateRow(providerStatsJson: SerializeProviders(
                new PlaybackProviderStat(
                    "backup-stats-1", 3, 20_000, 0, 0, 0, 0, 0, false))),
            providers);

        Assert.True(Assert.Single(session.Providers).IsBackup);
        Assert.Contains(PlaybackHistory.Issue.BackupUsed, session.Issues);
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
        // a harmless probe it would never be looked at.
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
    public void GroupIntoPlays_PreservesReadAheadAverageAndMinimum()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays([
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 10_000,
                    averageReadAheadBytes: 32_000_000,
                    minimumReadAheadBytes: 8_000_000),
                Providers()),
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start + 11_000,
                    endedAt: start + 31_000,
                    averageReadAheadBytes: 16_000_000,
                    minimumReadAheadBytes: 4_000_000),
                Providers()),
        ]));

        Assert.Equal(21_333_333, play.AverageReadAheadBytes);
        Assert.Equal(4_000_000, play.MinimumReadAheadBytes);
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
    public void DescribeIssues_UsesDurationRatherThanAWaitCount()
    {
        var longPlayWithDelays = PlaybackHistory.BuildSession(
            CreateRow(
                endedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_786_000,
                requestCount: 5,
                upstreamStalls: 25,
                maxUpstreamStallMs: 7_100,
                totalUpstreamStallMs: 80_000,
                upstreamWaitWallMs: 80_000,
                maxUpstreamWaitWallMs: 7_100),
            Providers());
        var shortPlayWithTheSamePattern = PlaybackHistory.BuildSession(
            CreateRow(
                endedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 40_000,
                requestCount: 5,
                upstreamStalls: 4,
                maxUpstreamStallMs: 4_400,
                totalUpstreamStallMs: 11_000,
                upstreamWaitWallMs: 11_000,
                maxUpstreamWaitWallMs: 4_400),
            Providers());
        var continuous = PlaybackHistory.BuildSession(
            CreateRow(
                endedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_786_000,
                requestCount: 5,
                upstreamStalls: 1,
                maxUpstreamStallMs: 12_000,
                totalUpstreamStallMs: 12_000,
                upstreamWaitWallMs: 12_000,
                maxUpstreamWaitWallMs: 12_000),
            Providers());

        Assert.DoesNotContain(PlaybackHistory.Issue.Stalled, longPlayWithDelays.Issues);
        Assert.Contains(PlaybackHistory.Issue.Stalled, shortPlayWithTheSamePattern.Issues);
        Assert.Contains(PlaybackHistory.Issue.Stalled, continuous.Issues);
    }

    [Fact]
    public void DescribeIssues_PlexProgressCanConfirmOrClearImpact()
    {
        var row = CreateRow(
            endedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 40_000,
            requestCount: 5,
            upstreamStalls: 4,
            maxUpstreamStallMs: 4_400,
            totalUpstreamStallMs: 11_000,
            upstreamWaitWallMs: 11_000,
            maxUpstreamWaitWallMs: 4_400);
        row.PlexPlaybackImpact = "progress-continued";
        var continued = PlaybackHistory.BuildSession(row, Providers());
        row.PlexPlaybackImpact = "buffering-observed";
        var buffered = PlaybackHistory.BuildSession(row, Providers());

        Assert.DoesNotContain(PlaybackHistory.Issue.Stalled, continued.Issues);
        Assert.DoesNotContain(PlaybackHistory.Issue.Buffering, continued.Issues);
        Assert.Contains(PlaybackHistory.Issue.Buffering, buffered.Issues);
        Assert.DoesNotContain(PlaybackHistory.Issue.Stalled, buffered.Issues);
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
    public void GroupIntoPlays_ClassifiesRepeatedRcloneTailReadsAsLikelyBackgroundActivity()
    {
        const long fileSize = 36_032_848_884;
        var start = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var sessions = Enumerable.Range(0, 5)
            .Select(index => PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start + index * 90_000,
                    endedAt: start + index * 90_000 + 6_000,
                    path: "/movies/there-will-be-blood.mkv",
                    clientUserAgent: "rclone/v1.74.3",
                    requestCount: 3,
                    upstreamStalls: index == 0 ? 4 : 0,
                    maxUpstreamStallMs: index == 0 ? 5_000 : 0,
                    bytesServed: 90_000_000,
                    bytesFetched: 130_000_000,
                    fileSize: fileSize,
                    maxOffset: fileSize,
                    endReason: ReadSession.EndReasonCode.Aborted),
                Providers()))
            .ToList();

        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(sessions));

        Assert.True(play.IsRcloneActivity);
        Assert.False(play.IsReliablePlayback);
        Assert.True(play.IsLikelyBackgroundActivity);
        Assert.False(play.IsProbe);
        Assert.False(PlaybackHistory.MatchesFilter(play, "playback"));
        Assert.False(PlaybackHistory.MatchesFilter(play, "probes"));
        Assert.True(PlaybackHistory.MatchesFilter(play, "mount"));
        // Source problems remain queryable even when the activity is background work.
        Assert.True(PlaybackHistory.MatchesFilter(play, "issues"));
    }

    [Fact]
    public void GroupIntoPlays_ClassifiesConcurrentRcloneBulkReadsAndTheirContinuation()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds();
        const string rclone = "rclone/v1.74.3";
        const long supergirlSize = 20_024_673_519;
        const long trumanSize = 60_016_333_472;
        var supergirlEnd = start + (long)TimeSpan.FromMinutes(27).TotalMilliseconds;
        var trumanStart = start + 10_000;
        var trumanEnd = trumanStart + (long)TimeSpan.FromMinutes(60).TotalMilliseconds;
        var trumanResume = trumanEnd + (long)TimeSpan.FromMinutes(17).TotalMilliseconds;

        var plays = PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: supergirlEnd,
                    path: "/movies/supergirl.mkv",
                    fileName: "Supergirl.mkv",
                    clientUserAgent: rclone,
                    requestCount: 84,
                    bytesServed: 19_760_000_000,
                    bytesFetched: 21_100_000_000,
                    fileSize: supergirlSize,
                    maxOffset: supergirlSize),
                Providers()),
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: trumanStart,
                    endedAt: trumanEnd,
                    path: "/movies/truman-show.mkv",
                    fileName: "The.Truman.Show.mkv",
                    clientUserAgent: rclone,
                    requestCount: 184,
                    bytesServed: 48_360_000_000,
                    bytesFetched: 51_400_000_000,
                    fileSize: trumanSize,
                    maxOffset: trumanSize,
                    endReason: ReadSession.EndReasonCode.Aborted),
                Providers()),
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: trumanResume,
                    endedAt: trumanResume + (long)TimeSpan.FromMinutes(17).TotalMilliseconds,
                    path: "/movies/truman-show.mkv",
                    fileName: "The.Truman.Show.mkv",
                    clientUserAgent: rclone,
                    requestCount: 46,
                    bytesServed: 11_670_000_000,
                    bytesFetched: 12_500_000_000,
                    fileSize: trumanSize,
                    maxOffset: trumanSize),
                Providers()),
        ]);

        Assert.Equal(3, plays.Count);
        Assert.All(plays, play =>
        {
            Assert.True(play.IsRcloneActivity);
            Assert.False(play.IsReliablePlayback);
            Assert.True(play.IsLikelyBackgroundActivity);
            Assert.True(PlaybackHistory.MatchesFilter(play, "mount"));
            Assert.False(PlaybackHistory.MatchesFilter(play, "probes"));
            Assert.False(PlaybackHistory.MatchesFilter(play, "playback"));
        });
        Assert.Equal(2, plays.Count(play => play.Title == "The.Truman.Show.mkv"));
    }

    [Fact]
    public void GroupIntoPlays_LeavesBackToBackRcloneBulkReadsUnclassified()
    {
        const long fileSize = 10_000_000_000;
        var start = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        var firstEnd = start + (long)TimeSpan.FromMinutes(45).TotalMilliseconds;
        var plays = PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: firstEnd,
                    path: "/shows/episode-one.mkv",
                    clientUserAgent: "rclone/v1.74.3",
                    bytesServed: 9_500_000_000,
                    bytesFetched: 9_800_000_000,
                    fileSize: fileSize,
                    maxOffset: fileSize),
                Providers()),
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: firstEnd + 10_000,
                    endedAt: firstEnd + (long)TimeSpan.FromMinutes(46).TotalMilliseconds,
                    path: "/shows/episode-two.mkv",
                    clientUserAgent: "rclone/v1.74.3",
                    bytesServed: 9_500_000_000,
                    bytesFetched: 9_800_000_000,
                    fileSize: fileSize,
                    maxOffset: fileSize),
                Providers()),
        ]);

        Assert.Equal(2, plays.Count);
        Assert.All(plays, play =>
        {
            Assert.True(play.IsRcloneActivity);
            Assert.False(play.IsReliablePlayback);
            Assert.False(play.IsLikelyBackgroundActivity);
            Assert.False(PlaybackHistory.MatchesFilter(play, "playback"));
            Assert.True(PlaybackHistory.MatchesFilter(play, "mount"));
            Assert.False(PlaybackHistory.MatchesFilter(play, "probes"));
        });
    }

    [Fact]
    public void GroupIntoPlays_LeavesAnIsolatedLargeRcloneReadUnclassified()
    {
        const long fileSize = 40_000_000_000;
        var start = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + (long)TimeSpan.FromMinutes(90).TotalMilliseconds,
                    path: "/movies/one-view.mkv",
                    clientUserAgent: "rclone/v1.74.3",
                    bytesServed: 38_000_000_000,
                    bytesFetched: 39_000_000_000,
                    fileSize: fileSize,
                    maxOffset: fileSize),
                Providers()),
        ]));

        Assert.True(play.IsRcloneActivity);
        Assert.False(play.IsReliablePlayback);
        Assert.False(play.IsLikelyBackgroundActivity);
        Assert.False(PlaybackHistory.MatchesFilter(play, "playback"));
        Assert.True(PlaybackHistory.MatchesFilter(play, "mount"));
        Assert.False(PlaybackHistory.MatchesFilter(play, "probes"));
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
    public void PlexAttributionEnrichesExistingMountRowsWithoutCreatingHistory()
    {
        var exactRow = CreateRow(
            clientUserAgent: "rclone/v1.74.3",
            bytesServed: 50_000_000);
        exactRow.PlexPurpose = "playback";
        exactRow.PlexConfidence = "exact-path";
        exactRow.PlexProduct = "Plex Web 4.160.0";
        exactRow.PlexPlatform = "Chrome";
        exactRow.PlexPlayer = "Chrome";
        exactRow.PlexRatingKey = "42";
        exactRow.PlexIsTranscode = true;

        var exact = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(exactRow, Providers()),
        ]));

        Assert.Equal("playback", exact.PlexPurpose);
        Assert.Equal("exact-path", exact.PlexConfidence);
        Assert.Equal("Plex Web 4.160.0", exact.PlexProduct);
        Assert.True(exact.PlexIsTranscode);
        Assert.True(exact.IsPlexPlayback);
        Assert.True(PlaybackHistory.MatchesFilter(exact, "playback"));
        Assert.True(PlaybackHistory.MatchesFilter(exact, "mount"));

        var timeOnlyRow = CreateRow(
            clientUserAgent: "rclone/v1.74.3",
            bytesServed: 2_000_000);
        timeOnlyRow.PlexPurpose = "intro-detection";
        timeOnlyRow.PlexConfidence = "time-only";
        var timeOnly = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(timeOnlyRow, Providers()),
        ]));

        Assert.False(timeOnly.IsPlexPlayback);
        Assert.False(PlaybackHistory.MatchesFilter(timeOnly, "playback"));
        Assert.True(PlaybackHistory.MatchesFilter(timeOnly, "mount"));

        timeOnlyRow.PlexPurpose = "playback";
        var probablePlayback = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(timeOnlyRow, Providers()),
        ]));

        Assert.True(probablePlayback.IsPlexPlayback);
        Assert.True(PlaybackHistory.MatchesFilter(probablePlayback, "playback"));
        Assert.True(PlaybackHistory.MatchesFilter(probablePlayback, "mount"));
    }

    [Fact]
    public void MountPurpose_IdentifiesSymlinkResolutionAndDropsCoincidentalPlexPlayback()
    {
        var row = CreateRow(
            fileName: "Pokemon.S01E01.Pokemon.I.Choose.You.mkv.rclonelink",
            clientUserAgent: "rclone/v1.74.3",
            bytesServed: 152,
            bytesFetched: 0);
        row.PlexPurpose = "playback";
        row.PlexConfidence = "time-only";
        row.PlexDetail = "Unrelated title";

        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(row, Providers()),
        ]));

        Assert.Equal("symlink-resolution", play.MountPurpose);
        Assert.True(play.IsProbe);
        Assert.True(play.IsLikelyBackgroundActivity);
        Assert.Equal("playback", play.PlexPurpose);
        Assert.False(play.IsPlexPlayback);
        Assert.True(PlaybackHistory.MatchesFilter(play, "mount"));
        Assert.True(PlaybackHistory.MatchesFilter(play, "probes"));
        Assert.False(PlaybackHistory.MatchesFilter(play, "playback"));
    }

    [Fact]
    public void MountPurpose_IdentifiesNewMultiFileImportBatchAndBeatsTimeOnlyPlex()
    {
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var historyItemId = Guid.NewGuid();
        var sessions = Enumerable.Range(1, 3).Select(index =>
        {
            var row = CreateRow(
                startedAt: (completedAt + index) * 1_000,
                endedAt: (completedAt + index + 5) * 1_000,
                fileName: $"Farscape.S01E{index:00}.mkv",
                clientUserAgent: "rclone/v1.74.3",
                requestCount: 2,
                bytesServed: 40_000_000,
                bytesFetched: 50_000_000,
                fileSize: 3_000_000_000,
                davItemId: Guid.NewGuid(),
                historyItemId: historyItemId);
            row.PlexPurpose = "playback";
            row.PlexConfidence = "time-only";
            row.PlexDetail = "Unrelated playing title";
            return PlaybackHistory.BuildSession(row, Providers());
        }).ToList();
        sessions.Add(PlaybackHistory.BuildSession(
            CreateRow(
                startedAt: (completedAt + 5 * 60) * 1_000,
                endedAt: (completedAt + 5 * 60 + 5) * 1_000,
                fileName: "Farscape.S01E04.mkv",
                clientUserAgent: "rclone/v1.74.3",
                bytesServed: 40_000_000,
                davItemId: Guid.NewGuid(),
                historyItemId: historyItemId),
            Providers()));

        var plays = PlaybackHistory.GroupIntoPlays(
            sessions,
            session => new PlaybackContentInfo(
                session.FileName,
                "Farscape.S01.Release",
                "tv",
                completedAt,
                "sonarr"));

        Assert.Equal(4, plays.Count);
        var imports = plays.Where(play => play.MountPurpose == "import-inspection").ToList();
        Assert.Equal(3, imports.Count);
        Assert.All(imports, play =>
        {
            Assert.Equal("import-inspection", play.MountPurpose);
            Assert.Equal(3, play.MountRelatedFileCount);
            Assert.Equal(completedAt, play.MountCompletedAtUnix);
            Assert.Equal("sonarr", play.SubmissionSource);
            Assert.True(play.IsLikelyBackgroundActivity);
            Assert.False(play.IsPlexPlayback);
            Assert.False(PlaybackHistory.MatchesFilter(play, "playback"));
            Assert.True(PlaybackHistory.MatchesFilter(play, "mount"));
        });
        Assert.Null(Assert.Single(
            plays,
            play => play.Title == "Farscape.S01E04.mkv").MountPurpose);
    }

    [Fact]
    public void MountPurpose_IdentifiesSingleFileImportFromMatchingSymlinkAndTailInspection()
    {
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var historyItemId = Guid.NewGuid();
        const long fileSize = 17_299_146_534;
        const string fileName =
            "Remarkably.Bright.Creatures.2026.2160p.NF.WEB-DL.mkv";
        var link = CreateRow(
            startedAt: (completedAt + 82) * 1_000,
            endedAt: (completedAt + 82) * 1_000 + 4,
            fileName: $"{fileName}.rclonelink",
            clientUserAgent: "rclone/v1.74.3",
            requestCount: 1,
            bytesServed: 76,
            fileSize: 76,
            maxOffset: 76);
        var media = CreateRow(
            startedAt: (completedAt + 82) * 1_000 + 60,
            endedAt: (completedAt + 88) * 1_000,
            fileName: fileName,
            clientUserAgent: "rclone/v1.74.3",
            requestCount: 3,
            bytesServed: 74_561_161,
            bytesFetched: 127_583_628,
            fileSize: fileSize,
            maxOffset: fileSize,
            endReason: ReadSession.EndReasonCode.Aborted,
            davItemId: Guid.NewGuid(),
            historyItemId: historyItemId);

        var plays = PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(link, Providers()),
            PlaybackHistory.BuildSession(media, Providers()),
        ], session => session.HistoryItemId is null
            ? null
            : new PlaybackContentInfo(
                fileName,
                "Remarkably.Bright.Creatures.Release",
                "movies",
                completedAt));

        var import = Assert.Single(
            plays,
            play => play.Title == fileName);
        Assert.Equal("import-inspection", import.MountPurpose);
        Assert.Equal(1, import.MountRelatedFileCount);
        Assert.Equal(completedAt, import.MountCompletedAtUnix);
        Assert.True(import.IsLikelyBackgroundActivity);
        Assert.False(PlaybackHistory.MatchesFilter(import, "playback"));
        Assert.Equal(
            "symlink-resolution",
            Assert.Single(plays, play => play.Title.EndsWith(".rclonelink")).MountPurpose);
    }

    [Fact]
    public void MountPurpose_LeavesSingleNewMediaReadUnclassified()
    {
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var row = CreateRow(
            startedAt: (completedAt + 2) * 1_000,
            endedAt: (completedAt + 8) * 1_000,
            fileName: "New.Movie.mkv",
            clientUserAgent: "rclone/v1.74.3",
            bytesServed: 40_000_000,
            davItemId: Guid.NewGuid(),
            historyItemId: Guid.NewGuid());

        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(row, Providers()),
        ], _ => new PlaybackContentInfo(
            "New.Movie.mkv",
            "New.Movie.Release",
            "movies",
            completedAt)));

        Assert.Null(play.MountPurpose);
        Assert.False(play.IsLikelyBackgroundActivity);
    }

    [Fact]
    public void MountPurpose_IdentifiesZeroTransferTailBurstAsAnalysisProbe()
    {
        const long fileSize = 29_241_474_013;
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = CreateRow(
            startedAt: startedAt,
            endedAt: startedAt + 306,
            clientUserAgent: "rclone/v1.74.3",
            requestCount: 15,
            bytesServed: 0,
            bytesFetched: 0,
            fileSize: fileSize,
            maxOffset: 29_241_470_976);

        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(row, Providers()),
        ]));

        Assert.Equal("analysis-probe", play.MountPurpose);
        Assert.True(play.IsProbe);
        Assert.True(play.IsLikelyBackgroundActivity);
        Assert.True(PlaybackHistory.MatchesFilter(play, "mount"));
        Assert.True(PlaybackHistory.MatchesFilter(play, "probes"));
        Assert.False(PlaybackHistory.MatchesFilter(play, "playback"));
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
        Assert.True(PlaybackHistory.MatchesFilter(play, "probes"));
    }

    [Fact]
    public void MatchesFilter_SeparatesTinyDirectProbesFromViewing()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // A tiny read: kilobytes, and it can be slow without revealing why.
        var probe = Assert.Single(PlaybackHistory.GroupIntoPlays(
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

        Assert.True(probe.IsProbe);
        Assert.False(probe.IsReliablePlayback);
        Assert.False(watched.IsProbe);
        Assert.True(watched.IsReliablePlayback);
        Assert.True(PlaybackHistory.MatchesFilter(probe, "probes"));
        // Keep the old query value working for API callers.
        Assert.True(PlaybackHistory.MatchesFilter(probe, "scans"));
        Assert.False(PlaybackHistory.MatchesFilter(probe, "playback"));
        Assert.True(PlaybackHistory.MatchesFilter(watched, "playback"));
        Assert.True(PlaybackHistory.MatchesFilter(watched, "plays"));
        Assert.True(PlaybackHistory.MatchesFilter(probe, "all"));
    }

    [Theory]
    [InlineData("Dalvik/2.1.0 (Linux; U; Android 16)")]
    [InlineData("stagefright/1.2 (Linux;Android 16)")]
    [InlineData("Mozilla/5.0 Chrome/138")]
    [InlineData("SomeAutomation/1.0")]
    public void MatchesFilter_SubstantialDirectReadIsPlaybackRegardlessOfUserAgent(
        string userAgent)
    {
        var play = Assert.Single(PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    clientUserAgent: userAgent,
                    bytesServed: 54_000_000),
                Providers()),
        ]));

        Assert.False(play.IsProbe);
        Assert.False(play.IsRcloneActivity);
        Assert.True(play.IsReliablePlayback);
        Assert.True(PlaybackHistory.MatchesFilter(play, "playback"));
    }

    [Fact]
    public void GroupIntoPlays_MergesChangingDirectUserAgentsIntoOnePlayback()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        var plays = PlaybackHistory.GroupIntoPlays(
        [
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start,
                    endedAt: start + 7_000,
                    clientUserAgent: "stagefright/1.2 (Linux;Android 16)",
                    bytesServed: 47_500_000),
                Providers()),
            PlaybackHistory.BuildSession(
                CreateRow(
                    startedAt: start + 2_000,
                    endedAt: start + 35_000,
                    clientUserAgent: "Dalvik/2.1.0 (Linux; U; Android 16)",
                    bytesServed: 113_000_000),
                Providers()),
        ]);

        var play = Assert.Single(plays);
        Assert.True(play.IsReliablePlayback);
        Assert.Equal(160_500_000, play.BytesServed);
        Assert.Equal(2, play.Sessions.Count);
        // The agent that carried most of the bytes is the useful display value,
        // while both raw agents remain visible in the session details.
        Assert.StartsWith("Dalvik/", play.ClientUserAgent);
    }

    [Fact]
    public void MountProbesRemainVisibleInBothUsefulFilters()
    {
        static GetPlaybackSessionsResponse.PlayDto Activity(
            string userAgent,
            long bytesServed) =>
            Assert.Single(PlaybackHistory.GroupIntoPlays(
            [
                PlaybackHistory.BuildSession(
                    CreateRow(
                        clientUserAgent: userAgent,
                        bytesServed: bytesServed),
                    Providers()),
            ]));

        var activities = new[]
        {
            Activity("VLC/3.0.21", 54_000_000),
            Activity("VLC/3.0.21", 330_000),
            Activity("rclone/v1.74.3", 54_000_000),
            Activity("rclone/v1.74.3", 330_000),
            Activity("SomeAutomation/1.0", 54_000_000),
        };

        Assert.True(PlaybackHistory.MatchesFilter(activities[3], "probes"));
        Assert.True(PlaybackHistory.MatchesFilter(activities[3], "mount"));
        Assert.False(PlaybackHistory.MatchesFilter(activities[3], "playback"));
    }

    [Fact]
    public void MatchesFilter_TinyButTroubledReadsStayVisible()
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // A stream that died after 20 KB is the most interesting row on the
        // page — it must never be filed away as a harmless probe.
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
        Assert.True(PlaybackHistory.MatchesFilter(died, "playback"));
        Assert.True(PlaybackHistory.MatchesFilter(stalled, "playback"));
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
        long upstreamWaitWallMs = 0,
        int maxUpstreamWaitWallMs = 0,
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
        long? averageReadAheadBytes = null,
        long? minimumReadAheadBytes = null,
        long bytesServed = 0,
        long? fileSize = null,
        long maxOffset = 0,
        ReadSession.EndReasonCode endReason = ReadSession.EndReasonCode.Completed,
        string? errorNote = null,
        string? providerStatsJson = null,
        Guid? davItemId = null,
        Guid? historyItemId = null)
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
            DavItemId = davItemId,
            HistoryItemId = historyItemId,
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
            UpstreamWaitWallMs = upstreamWaitWallMs,
            MaxUpstreamWaitWallMs = maxUpstreamWaitWallMs,
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
            AverageReadAheadBytes = averageReadAheadBytes,
            MinimumReadAheadBytes = minimumReadAheadBytes,
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
