namespace NzbWebDAV.Services;

/// <summary>
/// Owns the counters shared by request diagnostics and durable session totals.
/// Request and session scopes have different lifetimes, but a wait, recovery,
/// backup outcome, or contention event must be accumulated identically in both.
/// </summary>
internal sealed class PlaybackMetricsAccumulator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, BackupTotals> _backups =
        new(StringComparer.OrdinalIgnoreCase);
    private int _upstreamStalls;
    private long _maxUpstreamStallMs;
    private long _totalUpstreamStallMs;
    private int _headOfLineStalls;
    private long _totalHeadOfLineStallMs;
    private int _activeUpstreamWaits;
    private int _downstreamStalls;
    private long _maxDownstreamStallMs;
    private long _totalDownstreamStallMs;
    private int _activeDownstreamWaits;
    private int _fallbackRescues;
    private int _providerRotations;
    private int _fallbackBudgetExhaustions;
    private int _cacheHits;
    private int _cacheMisses;
    private int _connectionPermitWaits;
    private long _maxConnectionPermitWaitMs;
    private int _providerPoolWaits;
    private long _maxProviderPoolWaitMs;
    private int _zeroFilledSegments;
    private long _zeroFilledBytes;
    private int _bodyStallRecoveries;

    public PlaybackWaitUpdate RecordWait(
        bool isUpstream,
        long deltaMs,
        long totalElapsedMs,
        bool isNewWait,
        bool headOfLine)
    {
        lock (_lock)
        {
            if (isUpstream)
            {
                if (isNewWait) _upstreamStalls++;
                if (isNewWait && headOfLine) _headOfLineStalls++;
                if (headOfLine) _totalHeadOfLineStallMs += Math.Max(0, deltaMs);
                _maxUpstreamStallMs = Math.Max(_maxUpstreamStallMs, totalElapsedMs);
                _totalUpstreamStallMs += Math.Max(0, deltaMs);
                return new PlaybackWaitUpdate(_upstreamStalls, _maxUpstreamStallMs);
            }

            if (isNewWait) _downstreamStalls++;
            _maxDownstreamStallMs = Math.Max(_maxDownstreamStallMs, totalElapsedMs);
            _totalDownstreamStallMs += Math.Max(0, deltaMs);
            return new PlaybackWaitUpdate(_downstreamStalls, _maxDownstreamStallMs);
        }
    }

    public void BeginWait(bool isUpstream)
    {
        lock (_lock)
        {
            if (isUpstream)
                _activeUpstreamWaits++;
            else
                _activeDownstreamWaits++;
        }
    }

    public void EndWait(bool isUpstream)
    {
        lock (_lock)
        {
            ref var activeWaits = ref (isUpstream
                ? ref _activeUpstreamWaits
                : ref _activeDownstreamWaits);
            if (activeWaits > 0) activeWaits--;
        }
    }

    public int RecordFallbackRescue()
    {
        lock (_lock)
            return ++_fallbackRescues;
    }

    public void RecordProviderRotation()
    {
        lock (_lock)
            _providerRotations++;
    }

    public void RecordFallbackBudgetExhaustion()
    {
        lock (_lock)
            _fallbackBudgetExhaustions++;
    }

    public void RecordCacheHit()
    {
        lock (_lock)
            _cacheHits++;
    }

    public void RecordCacheMiss()
    {
        lock (_lock)
            _cacheMisses++;
    }

    public PlaybackWaitUpdate RecordConnectionPermitWait(long elapsedMs)
    {
        lock (_lock)
        {
            _connectionPermitWaits++;
            _maxConnectionPermitWaitMs = Math.Max(_maxConnectionPermitWaitMs, elapsedMs);
            return new PlaybackWaitUpdate(
                _connectionPermitWaits,
                _maxConnectionPermitWaitMs);
        }
    }

    public PlaybackWaitUpdate RecordProviderPoolWait(long elapsedMs)
    {
        lock (_lock)
        {
            _providerPoolWaits++;
            _maxProviderPoolWaitMs = Math.Max(_maxProviderPoolWaitMs, elapsedMs);
            return new PlaybackWaitUpdate(_providerPoolWaits, _maxProviderPoolWaitMs);
        }
    }

    public void RecordZeroFill(long bytes)
    {
        lock (_lock)
        {
            _zeroFilledSegments++;
            _zeroFilledBytes += Math.Max(0, bytes);
        }
    }

    public int RecordBodyStallRecovery()
    {
        lock (_lock)
            return ++_bodyStallRecoveries;
    }

    public long RecordBackupAttempt(string providerId, string providerHost)
    {
        lock (_lock)
        {
            var backup = GetOrAddBackup(providerId, providerHost);
            return ++backup.Attempts;
        }
    }

    public bool RecordBackupOutcome(
        string providerId,
        string providerHost,
        string outcome)
    {
        lock (_lock)
        {
            var backup = GetOrAddBackup(providerId, providerHost);
            switch (outcome)
            {
                case "rescued":
                    return ++backup.Rescued == 1;
                case "missing":
                    backup.Missing++;
                    break;
                case "timeout":
                    backup.Timeouts++;
                    break;
                default:
                    backup.Errors++;
                    break;
            }

            return false;
        }
    }

    public void Fold(PlaybackRequestDelta delta)
    {
        lock (_lock)
        {
            _fallbackRescues += delta.FallbackRescues;
            _providerRotations += delta.ProviderRotations;
            _fallbackBudgetExhaustions += delta.FallbackBudgetExhaustions;
            _cacheHits += delta.CacheHits;
            _cacheMisses += delta.CacheMisses;
            _connectionPermitWaits += delta.ConnectionPermitWaits;
            _maxConnectionPermitWaitMs =
                Math.Max(_maxConnectionPermitWaitMs, delta.MaxConnectionPermitWaitMs);
            _providerPoolWaits += delta.ProviderPoolWaits;
            _maxProviderPoolWaitMs =
                Math.Max(_maxProviderPoolWaitMs, delta.MaxProviderPoolWaitMs);
            _zeroFilledSegments += delta.ZeroFilledSegments;
            _zeroFilledBytes += delta.ZeroFilledBytes;
            _bodyStallRecoveries += delta.BodyStallRecoveries;

            foreach (var backup in delta.BackupProviders)
            {
                if (string.IsNullOrWhiteSpace(backup.ProviderId)) continue;
                var current = GetOrAddBackup(backup.ProviderId, backup.Host);
                current.Attempts += backup.Attempts;
                current.Rescued += backup.Rescued;
                current.Missing += backup.Missing;
                current.Timeouts += backup.Timeouts;
                current.Errors += backup.Errors;
            }
        }
    }

    public PlaybackMetricsSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new PlaybackMetricsSnapshot(
                _upstreamStalls,
                _maxUpstreamStallMs,
                _totalUpstreamStallMs,
                _headOfLineStalls,
                _totalHeadOfLineStallMs,
                _activeUpstreamWaits,
                _downstreamStalls,
                _maxDownstreamStallMs,
                _totalDownstreamStallMs,
                _activeDownstreamWaits,
                _fallbackRescues,
                _providerRotations,
                _fallbackBudgetExhaustions,
                _cacheHits,
                _cacheMisses,
                _connectionPermitWaits,
                _maxConnectionPermitWaitMs,
                _providerPoolWaits,
                _maxProviderPoolWaitMs,
                _zeroFilledSegments,
                _zeroFilledBytes,
                _bodyStallRecoveries,
                _backups
                    .Select(x => new PlaybackBackupProviderStat(
                        x.Key,
                        x.Value.Host,
                        x.Value.Attempts,
                        x.Value.Rescued,
                        x.Value.Missing,
                        x.Value.Timeouts,
                        x.Value.Errors))
                    .OrderByDescending(x => x.Rescued)
                    .ThenBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }
    }

    private BackupTotals GetOrAddBackup(string providerId, string providerHost)
    {
        if (!_backups.TryGetValue(providerId, out var backup))
        {
            backup = new BackupTotals(providerHost);
            _backups[providerId] = backup;
        }
        else if (string.IsNullOrWhiteSpace(backup.Host) &&
                 !string.IsNullOrWhiteSpace(providerHost))
        {
            backup.Host = providerHost;
        }

        return backup;
    }

    private sealed class BackupTotals(string host)
    {
        public string Host { get; set; } = host;
        public long Attempts;
        public long Rescued;
        public long Missing;
        public long Timeouts;
        public long Errors;
    }
}
