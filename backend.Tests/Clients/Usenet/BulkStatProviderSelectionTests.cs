using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class BulkStatProviderSelectionTests
{
    [Theory]
    [InlineData(31, 32, 300, 35, true)]
    [InlineData(29, 32, 100, 35, true)]
    [InlineData(28, 32, 1000, 35, false)]
    [InlineData(2, 32, 190, 21, false)]
    [InlineData(31, 32, 30, 35, false)]
    public void AdmitsOnlyFastHighCoveragePartialProviders(
        int found,
        int received,
        double statRate,
        double fallbackRate,
        bool expected)
    {
        Assert.Equal(expected, MultiProviderNntpClient.IsPartialStatProviderWorthwhile(
            found, received, statRate, fallbackRate));
    }
}
