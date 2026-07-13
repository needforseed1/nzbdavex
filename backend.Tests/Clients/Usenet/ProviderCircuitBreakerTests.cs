using System.Reflection;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ProviderCircuitBreakerTests
{
    [Fact]
    public void CooldownAllowsOnlyOneHalfOpenProbe()
    {
        var breaker = new ProviderCircuitBreaker("test");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);

        typeof(ProviderCircuitBreaker)
            .GetField("_trippedUntilMs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(breaker, Environment.TickCount64 - 1);

        var admitted = 0;
        Parallel.For(0, 32, _ =>
        {
            if (breaker.TryBeginAttempt(out var probe) && probe)
                Interlocked.Increment(ref admitted);
        });

        Assert.Equal(1, admitted);
        Assert.True(breaker.IsTripped);
    }

    [Fact]
    public void InFlightSuccessResetsTrippedBreaker()
    {
        var breaker = new ProviderCircuitBreaker("test");
        Assert.True(breaker.TryBeginAttempt(out var halfOpenProbe));
        Assert.False(halfOpenProbe);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess(halfOpenProbe);

        Assert.False(breaker.IsTripped);
        Assert.True(breaker.TryBeginAttempt(out var nextProbe));
        Assert.False(nextProbe);
        breaker.EndAttempt(nextProbe);
    }

    [Fact]
    public void SuccessfulHalfOpenProbeResetsBreaker()
    {
        var breaker = new ProviderCircuitBreaker("test");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        typeof(ProviderCircuitBreaker)
            .GetField("_trippedUntilMs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(breaker, Environment.TickCount64 - 1);

        Assert.True(breaker.TryBeginAttempt(out var halfOpenProbe));
        Assert.True(halfOpenProbe);
        breaker.RecordSuccess(halfOpenProbe);
        breaker.EndAttempt(halfOpenProbe);

        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void SmallFailureBurstDoesNotTripLargeConcurrentWorkload()
    {
        var breaker = new ProviderCircuitBreaker("test");
        var attempts = Enumerable.Range(0, 100)
            .Select(_ =>
            {
                Assert.True(breaker.TryBeginAttempt(out var probe));
                Assert.False(probe);
                return probe;
            })
            .ToArray();

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.False(breaker.IsTripped);

        breaker.RecordSuccess(false);
        foreach (var attempt in attempts) breaker.EndAttempt(attempt);
        Assert.False(breaker.IsTripped);
    }

    [Fact]
    public void WidespreadConcurrentFailuresStillTripPromptly()
    {
        var breaker = new ProviderCircuitBreaker("test");
        var attempts = Enumerable.Range(0, 100)
            .Select(_ =>
            {
                Assert.True(breaker.TryBeginAttempt(out var probe));
                return probe;
            })
            .ToArray();

        for (var i = 0; i < 11; i++) breaker.RecordFailure();
        Assert.False(breaker.IsTripped);

        breaker.RecordFailure();
        Assert.True(breaker.IsTripped);

        foreach (var attempt in attempts) breaker.EndAttempt(attempt);
    }

    [Theory]
    [InlineData(72, 12)]
    [InlineData(34, 7)]
    public void SuccessfulTailRequestRecoversProviderAfterReportedFailureBurst(
        int activeAttempts,
        int failures)
    {
        var breaker = new ProviderCircuitBreaker("test");
        var attempts = Enumerable.Range(0, activeAttempts)
            .Select(_ =>
            {
                Assert.True(breaker.TryBeginAttempt(out var probe));
                Assert.False(probe);
                return probe;
            })
            .ToArray();

        for (var i = 0; i < failures; i++) breaker.RecordFailure();
        Assert.True(breaker.IsTripped);

        breaker.RecordSuccess(false);

        Assert.False(breaker.IsTripped);
        Assert.True(breaker.TryBeginAttempt(out var recoveryProbe));
        Assert.False(recoveryProbe);
        breaker.EndAttempt(recoveryProbe);
        foreach (var attempt in attempts) breaker.EndAttempt(attempt);
    }

    [Fact]
    public void ProviderRemainsTrippedWhenEntireConcurrentWorkloadFails()
    {
        var breaker = new ProviderCircuitBreaker("test");
        var attempts = Enumerable.Range(0, 72)
            .Select(_ =>
            {
                Assert.True(breaker.TryBeginAttempt(out var probe));
                return probe;
            })
            .ToArray();

        for (var i = 0; i < 12; i++) breaker.RecordFailure();

        Assert.True(breaker.IsTripped);
        Assert.False(breaker.TryBeginAttempt(out _));
        foreach (var attempt in attempts) breaker.EndAttempt(attempt);
    }

    [Fact]
    public void SerialWorkloadRetainsThreeFailureThreshold()
    {
        var breaker = new ProviderCircuitBreaker("test");

        for (var i = 0; i < 3; i++)
        {
            Assert.True(breaker.TryBeginAttempt(out var probe));
            breaker.RecordFailure(probe);
            breaker.EndAttempt(probe);
        }

        Assert.True(breaker.IsTripped);
    }
}
