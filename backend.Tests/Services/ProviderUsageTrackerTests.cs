using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ProviderUsageTrackerTests
{
    [Fact]
    public void ToDisplayHosts_ReplacesStableIdsWithConfiguredHosts()
    {
        var providers = new[]
        {
            Provider("aaa3cc10-c54e-35d4-2079-c173b6d71879", "news.eweka.nl"),
            Provider("516d64b0-d4a3-4ba5-11c1-fb2455486143", "news.usenet.farm"),
        };
        var usage = new Dictionary<string, long>
        {
            [providers[0].Id] = 164,
            [providers[1].Id] = 162,
        };

        var display = ProviderUsageTracker.ToDisplayHosts(usage, providers);

        Assert.Equal(164, display["news.eweka.nl"]);
        Assert.Equal(162, display["news.usenet.farm"]);
        Assert.DoesNotContain(providers[0].Id, display.Keys);
        Assert.DoesNotContain(providers[1].Id, display.Keys);
    }

    [Fact]
    public void ToDisplayHosts_PreservesLegacyHostsAndAggregatesSharedHosts()
    {
        var providers = new[] { Provider("provider-id", "news.example.com") };
        var usage = new Dictionary<string, long>
        {
            ["provider-id"] = 10,
            ["news.example.com"] = 5,
            ["unknown-provider"] = 2,
        };

        var display = ProviderUsageTracker.ToDisplayHosts(usage, providers);

        Assert.Equal(15, display["news.example.com"]);
        Assert.Equal(2, display["unknown-provider"]);
    }

    [Fact]
    public void HealthSnapshot_KeepsSharedHostAccountsSeparate()
    {
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using (tracker.BeginScope(queueId))
        {
            tracker.BeginHealthCheck(113_377);
            tracker.RecordHealthProviderStat(HealthStat("farm-1", 32_937) with { ProbeStatus = "timeout" });
            tracker.RecordHealthProviderStat(HealthStat("farm-2", 34_183));
            tracker.RecordHealthProviderStat(HealthStat("farm-3", 33_554));
            tracker.CompleteHealthCheck(113_377, 0);
        }

        var snapshot = Assert.IsType<HealthCheckUsageSnapshot>(tracker.SnapshotHealthCheck(queueId));

        Assert.Equal(113_377, snapshot.TotalArticles);
        Assert.Equal(113_377, snapshot.FoundArticles);
        Assert.Equal(0, snapshot.MissingArticles);
        Assert.Equal(3, snapshot.Providers.Count);
        Assert.Equal(new[] { "farm-2", "farm-3", "farm-1" }, snapshot.Providers.Select(x => x.ProviderId));
        Assert.All(snapshot.Providers, stat => Assert.Equal("news.usenet.farm", stat.Host));
        Assert.Equal("timeout", snapshot.Providers.Single(x => x.ProviderId == "farm-1").ProbeStatus);
    }

    [Fact]
    public void HealthSnapshot_OlderJsonKeepsUnknownOutcomeNullable()
    {
        const string json = """{"TotalArticles":42,"Providers":[]}""";

        var snapshot = JsonSerializer.Deserialize<HealthCheckUsageSnapshot>(json);

        Assert.NotNull(snapshot);
        Assert.Equal(42, snapshot.TotalArticles);
        Assert.Null(snapshot.FoundArticles);
        Assert.Null(snapshot.MissingArticles);
    }

    [Fact]
    public void PrepSnapshot_PreservesStageTimingsAndStableProviderIds()
    {
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        var expected = new PrepUsageSnapshot(
            1_424, 150, 18_087, 8_839, 660, 489, 64, true, 2,
            [
                new PrepProviderStat("newshosting-id", 924, 15_138_816),
                new PrepProviderStat("eweka-id", 500, 8_192_000),
            ],
            "processors");

        using (tracker.BeginScope(queueId))
            tracker.RecordPrepStats(expected);

        var snapshot = Assert.IsType<PrepUsageSnapshot>(tracker.SnapshotPrep(queueId));

        Assert.Equal(expected, snapshot);
        Assert.Equal(new[] { "newshosting-id", "eweka-id" },
            snapshot.Providers.Select(x => x.ProviderId));
        Assert.Equal(1_424, snapshot.Providers.Sum(x => x.Articles));
        Assert.Equal(23_330_816, snapshot.Providers.Sum(x => x.Bytes));
        Assert.Equal("processors", snapshot.LastStage);
    }

    [Fact]
    public void PrepSnapshot_OlderJsonKeepsLastStageNullable()
    {
        const string json =
            """
            {"FileCount":1,"Connections":5,"QueueWaitMs":10,"FirstSegmentsMs":20,"Par2Ms":0,
             "RarMs":0,"ProcessorsMs":0,"LazyRarMounted":false,"FirstSegmentFallbacks":0,"Providers":[]}
            """;

        var snapshot = JsonSerializer.Deserialize<PrepUsageSnapshot>(json);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.LastStage);
    }

    [Fact]
    public void ByteSnapshot_AttributesReadBytesToTheActiveQueueScope()
    {
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using (tracker.BeginScope(queueId))
        using (tracker.BeginByteCapture())
        {
            tracker.RecordBytes("eweka-id", 16_384);
            tracker.RecordBytes("eweka-id", 4_096);
            tracker.RecordBytes("newshosting-id", 8_192);
        }

        var bytes = tracker.SnapshotBytes(queueId);

        Assert.Equal(20_480, bytes["eweka-id"]);
        Assert.Equal(8_192, bytes["newshosting-id"]);
    }

    [Fact]
    public void ByteSnapshot_IgnoresReadsOutsideExplicitCaptureWindow()
    {
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using (tracker.BeginScope(queueId))
            tracker.RecordBytes("eweka-id", 16_384);

        Assert.Empty(tracker.SnapshotBytes(queueId));
    }

    private static HealthProviderStat HealthStat(string id, long found) => new(
        id, "news.usenet.farm", true, 32, 32, 1, found, found, found, 0, 0, 100, 400);

    private static UsenetProviderConfig.ConnectionDetails Provider(string id, string host) => new()
    {
        Id = id,
        Type = ProviderType.Pooled,
        Host = host,
        Port = 563,
        UseSsl = true,
        User = "user",
        Pass = "pass",
        MaxConnections = 10,
    };
}
