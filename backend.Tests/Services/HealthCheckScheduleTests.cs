using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckScheduleTests
{
    private static readonly DateTimeOffset CheckedAt =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScheduleDoublesTheAgeOfTheRelease()
    {
        var releaseDate = CheckedAt - TimeSpan.FromDays(10);

        var next = HealthCheckService.GetNextHealthCheck(releaseDate, CheckedAt);

        Assert.Equal(releaseDate + TimeSpan.FromDays(20), next);
    }

    [Fact]
    public void MissingReleaseDateStillSchedulesTheNextCheck()
    {
        var next = HealthCheckService.GetNextHealthCheck(null, CheckedAt);

        // A null would keep the item permanently at the head of the queue.
        Assert.Equal(CheckedAt + HealthCheckService.UnknownReleaseDateRecheckInterval, next);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ReleaseDateAtOrAfterTheCheckFallsBackToTheFixedInterval(int daysAhead)
    {
        var releaseDate = CheckedAt + TimeSpan.FromDays(daysAhead);

        var next = HealthCheckService.GetNextHealthCheck(releaseDate, CheckedAt);

        // Doubling a non-positive age would schedule the next check in the past.
        Assert.Equal(CheckedAt + HealthCheckService.UnknownReleaseDateRecheckInterval, next);
    }

    [Fact]
    public void ScheduleIsAlwaysInTheFuture()
    {
        DateTimeOffset?[] releaseDates =
        [
            null,
            CheckedAt,
            CheckedAt + TimeSpan.FromHours(1),
            CheckedAt - TimeSpan.FromMilliseconds(1),
            CheckedAt - TimeSpan.FromDays(3650),
        ];

        foreach (var releaseDate in releaseDates)
            Assert.True(HealthCheckService.GetNextHealthCheck(releaseDate, CheckedAt) > CheckedAt);
    }
}
