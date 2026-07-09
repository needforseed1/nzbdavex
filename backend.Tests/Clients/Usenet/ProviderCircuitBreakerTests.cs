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
}
