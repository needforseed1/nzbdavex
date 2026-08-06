using NzbWebDAV.Clients.Plex;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Services;

public class PlexReadAttributionMonitorTests
{
    [Theory]
    [InlineData("library.update.section", "Updating Movies", null, "library-scan")]
    [InlineData("library.scan", "Scanning library", null, "library-scan")]
    [InlineData("media.analyze", "Detecting intros", null, "intro-detection")]
    [InlineData("media.analyze", "Detecting credits", null, "credits-detection")]
    [InlineData("media.generate.bif", "Generating video previews", null, "thumbnail-generation")]
    [InlineData("media.analyze", "Analyzing loudness", null, "loudness-analysis")]
    [InlineData("butler", "Butler tasks", "DeepMediaAnalysis", "deep-media-analysis")]
    [InlineData("butler.task", "Database backup", null, null)]
    public void ClassifiesOnlyUsefulReadPurposes(
        string type,
        string title,
        string? subtitle,
        string? expected)
    {
        var activity = new PlexActivityObservation
        {
            Key = "activity",
            Type = type,
            Title = title,
            Subtitle = subtitle,
        };

        Assert.Equal(expected, PlexReadAttributionMonitor.ClassifyActivity(activity));
    }

    [Fact]
    public void ExactMediaIdentityAttributesTheExistingRcloneRead()
    {
        var davItemId = Guid.NewGuid();
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        monitor.RecordSessions(
        [
            new PlexSessionObservation
            {
                SessionKey = "session-key",
                SessionId = "session-id",
                State = "playing",
                MediaPartPath = $"/mount/.ids/{davItemId}",
                RatingKey = "42",
                Product = "Plex Web",
                PlayerVersion = "4.160.0",
                Platform = "Chrome",
                PlayerTitle = "Chrome",
                IsTranscode = true,
            },
        ], new PlexServerInfo("Plex", "1", "server"), now);

        var attribution = monitor.Match(
            now - 1_000,
            now + 1_000,
            davItemId,
            "rclone/v1.70");

        Assert.NotNull(attribution);
        Assert.Equal("playback", attribution.Purpose);
        Assert.Equal("exact-path", attribution.Confidence);
        Assert.Equal("Plex Web 4.160.0", attribution.Product);
        Assert.Equal("42", attribution.RatingKey);
        Assert.True(attribution.IsTranscode);
        Assert.Null(monitor.Match(
            now - 1_000,
            now + 1_000,
            davItemId,
            "Infuse/8"));
    }

    [Fact]
    public void BackgroundActivityIsClearlyTimeOnly()
    {
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        monitor.RecordActivities(
        [
            new PlexActivityObservation
            {
                Key = "intro-job",
                Type = "media.analyze",
                Title = "Detecting intros",
                Subtitle = "TV Shows",
            },
        ], new PlexServerInfo("Plex", "1", "server"), now);

        var attribution = monitor.Match(
            now - 500,
            now + 500,
            Guid.NewGuid(),
            "rclone/v1.70");

        Assert.NotNull(attribution);
        Assert.Equal("intro-detection", attribution.Purpose);
        Assert.Equal("time-only", attribution.Confidence);
        Assert.Equal("Detecting intros · TV Shows", attribution.Detail);
    }

    [Fact]
    public void SimultaneousBackgroundJobsRemainUnattributed()
    {
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        monitor.RecordActivities(
        [
            new PlexActivityObservation
            {
                Key = "scan-one",
                Type = "library.update.section",
                Title = "Scanning Movies",
            },
            new PlexActivityObservation
            {
                Key = "scan-two",
                Type = "library.update.section",
                Title = "Scanning TV Shows",
            },
        ], new PlexServerInfo("Plex", "1", "server"), now);

        Assert.Null(monitor.Match(
            now - 500,
            now + 500,
            Guid.NewGuid(),
            "rclone/v1.70"));
    }

    [Fact]
    public void LongPausedStateWinsOverBriefPlayingState()
    {
        var davItemId = Guid.NewGuid();
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new PlexSessionObservation
        {
            SessionKey = "session-key",
            SessionId = "session-id",
            State = "playing",
            MediaPartPath = $"/mount/.ids/{davItemId}",
        };
        monitor.RecordSessions(
            [session],
            new PlexServerInfo("Plex", "1", "server"),
            now);
        for (var index = 1; index <= 4; index++)
            monitor.RecordSessions(
                [session with { State = "paused" }],
                new PlexServerInfo("Plex", "1", "server"),
                now + index * 2_000);

        var attribution = monitor.Match(
            now,
            now + 8_000,
            davItemId,
            "rclone/v1.70");

        Assert.NotNull(attribution);
        Assert.Equal("paused", attribution.Purpose);
        Assert.Equal("exact-path", attribution.Confidence);
    }

    [Fact]
    public void ExplicitBufferingDuringAnUpstreamWaitIsRetainedAsCompactImpact()
    {
        var davItemId = Guid.NewGuid();
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new PlexSessionObservation
        {
            SessionKey = "session-key",
            SessionId = "session-id",
            State = "playing",
            ViewOffsetMs = 10_000,
            MediaPartPath = $"/mount/.ids/{davItemId}",
        };
        monitor.RecordSessions([session], new PlexServerInfo("Plex", "1", "server"), now);
        monitor.RecordSessions(
            [session with { State = "buffering" }],
            new PlexServerInfo("Plex", "1", "server"),
            now + 2_000);
        monitor.RecordSessions(
            [session with { ViewOffsetMs = 12_000 }],
            new PlexServerInfo("Plex", "1", "server"),
            now + 4_000);

        var attribution = monitor.Match(
            now,
            now + 4_000,
            davItemId,
            "rclone/v1.70",
            [new PlaybackWaitWindow(now + 1_000, now + 3_000)]);

        Assert.NotNull(attribution);
        Assert.Equal("playback", attribution.Purpose);
        Assert.Equal("buffering-observed", attribution.PlaybackImpact);
    }

    [Fact]
    public void StartupPrebufferingDoesNotClaimPlaybackWasInterrupted()
    {
        var davItemId = Guid.NewGuid();
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new PlexSessionObservation
        {
            SessionKey = "session-key",
            SessionId = "session-id",
            State = "buffering",
            ViewOffsetMs = 0,
            MediaPartPath = $"/mount/.ids/{davItemId}",
        };
        monitor.RecordSessions([session], new PlexServerInfo("Plex", "1", "server"), now);
        monitor.RecordSessions(
            [session with { State = "playing", ViewOffsetMs = 1_000 }],
            new PlexServerInfo("Plex", "1", "server"),
            now + 2_000);

        var attribution = monitor.Match(
            now,
            now + 2_000,
            davItemId,
            "rclone/v1.70",
            [new PlaybackWaitWindow(now, now + 2_000)]);

        Assert.NotNull(attribution);
        Assert.Null(attribution.PlaybackImpact);
    }

    [Fact]
    public void PlayingProgressThatStopsDuringAWaitAndResumesIsRetained()
    {
        var davItemId = Guid.NewGuid();
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new PlexSessionObservation
        {
            SessionKey = "session-key",
            SessionId = "session-id",
            State = "playing",
            ViewOffsetMs = 20_000,
            MediaPartPath = $"/mount/.ids/{davItemId}",
        };
        for (var index = 0; index <= 3; index++)
            monitor.RecordSessions(
                [session],
                new PlexServerInfo("Plex", "1", "server"),
                now + index * 2_000);
        monitor.RecordSessions(
            [session with { ViewOffsetMs = 23_000 }],
            new PlexServerInfo("Plex", "1", "server"),
            now + 8_000);

        var attribution = monitor.Match(
            now,
            now + 8_000,
            davItemId,
            "rclone/v1.70",
            [new PlaybackWaitWindow(now, now + 4_000)]);

        Assert.Equal("progress-stalled", attribution?.PlaybackImpact);
    }

    [Fact]
    public void ProgressAcrossEveryMaterialWaitIsRetainedAsContinued()
    {
        var davItemId = Guid.NewGuid();
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new PlexSessionObservation
        {
            SessionKey = "session-key",
            SessionId = "session-id",
            State = "playing",
            MediaPartPath = $"/mount/.ids/{davItemId}",
        };
        for (var index = 0; index <= 4; index++)
            monitor.RecordSessions(
                [session with { ViewOffsetMs = index * 2_000 }],
                new PlexServerInfo("Plex", "1", "server"),
                now + index * 2_000);

        var attribution = monitor.Match(
            now,
            now + 8_000,
            davItemId,
            "rclone/v1.70",
            [new PlaybackWaitWindow(now + 2_000, now + 6_000)]);

        Assert.Equal("progress-continued", attribution?.PlaybackImpact);
    }

    [Fact]
    public void TimeOnlyPausedSessionDoesNotClaimUnrelatedMountReads()
    {
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        monitor.RecordSessions(
        [
            new PlexSessionObservation
            {
                SessionKey = "paused-session",
                State = "paused",
                MediaPartPath = null,
                Title = "Supergirl",
            },
        ], new PlexServerInfo("Plex", "1", "server"), now);

        Assert.Null(monitor.Match(
            now - 500,
            now + 500,
            Guid.NewGuid(),
            "rclone/v1.70"));
    }

    [Fact]
    public void TimeOnlyPlayingSessionCanStillBeProbablePlayback()
    {
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        monitor.RecordSessions(
        [
            new PlexSessionObservation
            {
                SessionKey = "playing-session",
                State = "playing",
                MediaPartPath = null,
                Title = "Farscape",
            },
        ], new PlexServerInfo("Plex", "1", "server"), now);

        var attribution = monitor.Match(
            now - 500,
            now + 500,
            Guid.NewGuid(),
            "rclone/v1.70");

        Assert.NotNull(attribution);
        Assert.Equal("playback", attribution.Purpose);
        Assert.Equal("time-only", attribution.Confidence);
    }

    [Fact]
    public void TwoPlexSessionsForTheSameMediaRemainUnattributed()
    {
        var davItemId = Guid.NewGuid();
        using var client = new PlexClient();
        var monitor = CreateMonitor(client);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new PlexSessionObservation
        {
            SessionKey = "session-one",
            State = "playing",
            MediaPartPath = $"/mount/.ids/{davItemId}",
        };
        monitor.RecordSessions(
            [
                session,
                session with { SessionKey = "session-two" },
            ],
            new PlexServerInfo("Plex", "1", "server"),
            now);

        Assert.Null(monitor.Match(
            now - 500,
            now + 500,
            davItemId,
            "rclone/v1.70"));
    }

    private static PlexReadAttributionMonitor CreateMonitor(PlexClient client)
    {
        var config = new ConfigManager();
        return new PlexReadAttributionMonitor(
            config,
            client,
            new PlexPathResolver(config));
    }
}
