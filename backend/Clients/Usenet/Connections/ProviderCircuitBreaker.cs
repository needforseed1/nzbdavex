using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks consecutive connection failures for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// After tripping, the provider enters a cooldown period during which it is
/// skipped. When the cooldown expires, a single probe attempt is allowed.
/// If the probe succeeds, the breaker resets. If it fails, the cooldown
/// doubles (up to a cap) and the breaker re-trips.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    private readonly string _providerName;
    private readonly object _lock = new();

    private int _consecutiveFailures;
    private long _trippedUntilMs;
    private TimeSpan _currentCooldown = InitialCooldown;
    private bool _probeInFlight;

    public ProviderCircuitBreaker(string providerName)
    {
        _providerName = providerName;
    }

    public bool IsTripped
    {
        get
        {
            lock (_lock)
            {
                if (_trippedUntilMs == 0) return false;
                return Environment.TickCount64 < _trippedUntilMs || _probeInFlight;
            }
        }
    }

    public bool TryBeginAttempt(out bool halfOpenProbe)
    {
        lock (_lock)
        {
            halfOpenProbe = false;
            if (_trippedUntilMs == 0) return true;
            if (Environment.TickCount64 < _trippedUntilMs || _probeInFlight) return false;

            _probeInFlight = true;
            halfOpenProbe = true;
            return true;
        }
    }

    public void EndAttempt(bool halfOpenProbe)
    {
        if (!halfOpenProbe) return;
        lock (_lock)
            _probeInFlight = false;
    }

    public void RecordSuccess(bool halfOpenProbe)
    {
        lock (_lock)
        {
            // Requests admitted before another request tripped the breaker may
            // still complete successfully. They must not reopen the provider;
            // only the deliberately admitted half-open probe can do that.
            if (_trippedUntilMs > 0 && !halfOpenProbe) return;

            if (_consecutiveFailures > 0 || _trippedUntilMs > 0)
                Log.Information("Provider {Provider} recovered - circuit breaker reset.", _providerName);

            _consecutiveFailures = 0;
            _trippedUntilMs = 0;
            _currentCooldown = InitialCooldown;
            _probeInFlight = false;
        }
    }

    public void RecordFailure(bool halfOpenProbe = false)
    {
        lock (_lock)
        {
            // Ignore the tail of a concurrent failure burst after the breaker
            // has opened. Extending the cooldown is reserved for a failed
            // half-open probe after the current cooldown has elapsed.
            if (_trippedUntilMs > 0 && !halfOpenProbe) return;

            _consecutiveFailures++;
            _probeInFlight = false;

            if (_consecutiveFailures < FailureThreshold) return;

            _trippedUntilMs = Environment.TickCount64 + (long)_currentCooldown.TotalMilliseconds;
            Log.Warning(
                "Provider {Provider} tripped after {Failures} consecutive failures. " +
                "Skipping for {Cooldown}s.",
                _providerName, _consecutiveFailures, _currentCooldown.TotalSeconds);

            _currentCooldown = TimeSpan.FromMilliseconds(
                Math.Min(_currentCooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
        }
    }
}
