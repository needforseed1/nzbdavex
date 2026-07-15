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
    [InlineData(23, 32, true)]
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
}
