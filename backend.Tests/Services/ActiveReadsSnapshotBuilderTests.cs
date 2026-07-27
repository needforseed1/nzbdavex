using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ActiveReadsSnapshotBuilderTests
{
    [Fact]
    public void BuildPayload_PreservesTheLivePlaybackContractAndRates()
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        var usage = new ProviderUsageTracker();
        using (usage.BeginScope(id))
        {
            usage.RecordSuccess("provider-1");
            usage.RecordSuccess("provider-1");
        }

        var stats = new PlaybackSessionStats();
        stats.BeginWait(id, isUpstream: true);
        stats.RecordWait(
            id,
            isUpstream: true,
            deltaMs: 1_200,
            totalElapsedMs: 1_200,
            isNewWait: true);
        stats.RecordZeroFill(id, 750_000);
        stats.RecordBodyStallRecovery(id);

        var builder = new ActiveReadsSnapshotBuilder(
            usage,
            new ConfigManager(),
            stats);
        var entry = Session(id, now, bytesRead: 100);

        using var first = JsonDocument.Parse(builder.BuildPayload([entry], now));
        var read = Assert.Single(first.RootElement.GetProperty("reads").EnumerateArray());

        Assert.Equal(id, read.GetProperty("id").GetGuid());
        Assert.Equal("movie.mkv", read.GetProperty("fileName").GetString());
        Assert.Equal("/content/movie", read.GetProperty("path").GetString());
        Assert.Equal(100, read.GetProperty("bytesRead").GetInt64());
        Assert.Equal(250, read.GetProperty("currentOffset").GetInt64());
        Assert.Equal(0, read.GetProperty("bytesPerSecond").GetInt64());
        Assert.Equal(1, read.GetProperty("upstreamStalls").GetInt32());
        Assert.Equal(1_200, read.GetProperty("totalUpstreamStallMs").GetInt64());
        Assert.Equal(1, read.GetProperty("upstreamWaitsInProgress").GetInt32());
        Assert.Equal(1, read.GetProperty("zeroFilledSegments").GetInt32());
        Assert.Equal(1, read.GetProperty("bodyStallRecoveries").GetInt32());
        var provider = Assert.Single(read.GetProperty("providers").EnumerateArray());
        Assert.Equal("provider-1", provider.GetProperty("host").GetString());
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("nickname").ValueKind);
        Assert.Equal(2, provider.GetProperty("segments").GetInt64());

        var later = now.AddSeconds(2);
        using var second = JsonDocument.Parse(builder.BuildPayload(
            [entry with { BytesRead = 400, LastActivityAt = later }],
            later));
        Assert.Equal(
            150,
            Assert.Single(second.RootElement.GetProperty("reads").EnumerateArray())
                .GetProperty("bytesPerSecond")
                .GetInt64());

        builder.Forget(id);
        using var afterForget = JsonDocument.Parse(builder.BuildPayload(
            [entry with { BytesRead = 500, LastActivityAt = later.AddSeconds(1) }],
            later.AddSeconds(1)));
        Assert.Equal(
            0,
            Assert.Single(afterForget.RootElement.GetProperty("reads").EnumerateArray())
                .GetProperty("bytesPerSecond")
                .GetInt64());
    }

    private static ActiveReadSessionSnapshot Session(
        Guid id,
        DateTimeOffset now,
        long bytesRead) =>
        new(
            id,
            "/content/movie",
            "movie.mkv",
            1_000,
            "vlc",
            "10.0.0.5",
            null,
            null,
            ReadSession.EndReasonCode.Completed,
            now.AddSeconds(-5),
            now,
            bytesRead,
            CurrentOffset: 250,
            MaxOffset: 300);
}
