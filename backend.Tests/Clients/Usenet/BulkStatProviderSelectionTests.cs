using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class BulkStatProviderSelectionTests
{
    [Fact]
    public void BulkStatTiming_SumsLaneTimeForComparablePerLaneRate()
    {
        var stats = new MultiProviderNntpClient.BulkStatAttemptStats();
        stats.Record(100, 100, 100, 0, 200, false);
        stats.Record(200, 200, 200, 0, 200, false);
        stats.Record(100, 100, 100, 0, 100, false);

        var snapshot = stats.Snapshot();

        Assert.Equal(400, snapshot.Received);
        Assert.Equal(500, snapshot.ElapsedMs);
        Assert.Equal(800, snapshot.Received * 1_000 / snapshot.ElapsedMs);
    }

    [Theory]
    [InlineData(31, 32, true)]
    [InlineData(24, 32, true)]
    // A 32-article probe measures a genuinely 75%-covered provider anywhere
    // near 69%, so the admission bar stays well below the coverage it targets.
    [InlineData(22, 32, true)]
    [InlineData(16, 32, true)]
    [InlineData(15, 32, false)]
    [InlineData(2, 32, false)]
    [InlineData(0, 32, false)]
    [InlineData(32, 32, false)]
    public void AdmitsPartialProvidersAtFiftyPercentCoverage(
        int found,
        int received,
        bool expected)
    {
        Assert.Equal(expected, MultiProviderNntpClient.IsPartialStatProviderEligible(found, received));
    }

    [Theory]
    [InlineData(true, 32, 0, true)]
    [InlineData(true, 32, 1, false)]
    [InlineData(false, 0, 0, false)]
    [InlineData(true, 0, 0, false)]
    public void QuiescesWarmupOnlyForConfirmedZeroCoverage(
        bool probeSuccess,
        int received,
        int found,
        bool expected)
    {
        Assert.Equal(
            expected,
            MultiProviderNntpClient.ShouldQuiesceHealthPrewarm(
                probeSuccess,
                received,
                found));
    }

    [Fact]
    public void RecoveryBudgetGrowsWithTheIndeterminateWorkload()
    {
        var small = MultiProviderNntpClient.ResolveIndeterminateRecoveryBudget(4);
        var medium = MultiProviderNntpClient.ResolveIndeterminateRecoveryBudget(5_000);
        var huge = MultiProviderNntpClient.ResolveIndeterminateRecoveryBudget(1_000_000);

        Assert.Equal(TimeSpan.FromSeconds(25), small);
        Assert.Equal(TimeSpan.FromSeconds(65), medium);
        // Capped: one slow provider must not hold a health check open forever.
        Assert.Equal(TimeSpan.FromSeconds(120), huge);
        Assert.True(medium > small);
    }

    [Fact]
    public void StalledSocketsAreReportedButExcludedFromProviderFaults()
    {
        var stats = new MultiProviderNntpClient.BulkStatAttemptStats();
        stats.Record(64, 5, 5, 0, 2000, failed: true, providerFaulted: false);
        stats.Record(64, 0, 0, 0, 5000, failed: true, providerFaulted: true);

        var snapshot = stats.Snapshot();

        Assert.Equal(2, snapshot.Failures);
        Assert.Equal(1, snapshot.ProviderFaults);
        Assert.Equal(1, stats.ProviderFaultCount);
    }

    [Fact]
    public void RepeatedPartialStallsReduceLaneCapacityInSteps()
    {
        var backoff = new MultiProviderNntpClient.BulkStatLaneBackoff();

        var first = backoff.Observe(64, Snapshot(batches: 400, failures: 4));
        var duplicate = backoff.Observe(64, Snapshot(batches: 400, failures: 4));
        var second = backoff.Observe(64, Snapshot(batches: 800, failures: 8));

        Assert.NotNull(first);
        Assert.Equal(64, first.Value.PreviousLimit);
        Assert.Equal(48, first.Value.NewLimit);
        Assert.Null(duplicate);
        Assert.NotNull(second);
        Assert.Equal(48, second.Value.PreviousLimit);
        Assert.Equal(36, second.Value.NewLimit);
        Assert.Equal(36, backoff.LaneLimit);
    }

    [Fact]
    public void SparseStallsAndProviderFaultsDoNotReduceLaneCapacity()
    {
        var backoff = new MultiProviderNntpClient.BulkStatLaneBackoff();

        var sparse = backoff.Observe(64, Snapshot(batches: 1_000, failures: 4));
        var providerFaults = backoff.Observe(
            64, Snapshot(batches: 100, failures: 8, providerFaults: 8));

        Assert.Null(sparse);
        Assert.Null(providerFaults);
        Assert.Null(backoff.LaneLimit);
    }

    [Theory]
    [InlineData(38, 50, null, 42)]
    [InlineData(49, 50, null, 50)]
    [InlineData(0, 50, null, 5)]
    [InlineData(38, 50, 36, 36)]
    [InlineData(8, 4, null, 4)]
    public void ProviderLaneAdmissionAllowsOnlyABoundedGrowthWave(
        int liveConnections,
        int lowPriorityConnectionLimit,
        int? laneBackoffLimit,
        int expected)
    {
        var actual = MultiProviderNntpClient.ResolveHealthProviderLaneAdmissionLimit(
            liveConnections,
            lowPriorityConnectionLimit,
            laneBackoffLimit);

        Assert.Equal(expected, actual);
    }

    private static MultiProviderNntpClient.BulkStatAttemptSnapshot Snapshot(
        long batches,
        long failures,
        long providerFaults = 0) =>
        new(
            Batches: batches,
            Attempted: batches * 64,
            Received: batches * 64 - failures,
            Found: batches * 64 - failures,
            Missing: 0,
            Failures: failures,
            ElapsedMs: batches * 10,
            ProviderFaults: providerFaults);
}
