using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class WatchdogNzbRetryServiceTests
{
    [Theory]
    [InlineData("watchdog-manual-retry:42", true, 42)]
    [InlineData("watchdog-manual-retry:0", true, 0)]
    [InlineData("watchdog-manual-retry:not-a-number", false, 0)]
    [InlineData("sonarr", false, 0)]
    [InlineData(null, false, 0)]
    public void ParsesOnlyWatchdogRetrySubmissionSources(string? source, bool expected, long expectedId)
    {
        var parsed = WatchdogNzbRetryService.TryParseRetryEventId(source, out var eventId);

        Assert.Equal(expected, parsed);
        Assert.Equal(expectedId, eventId);
    }
}
