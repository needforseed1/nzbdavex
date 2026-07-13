using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Glue between the persistent metrics rollups and the in-memory byte tracker
/// for the per-provider data cap. Two responsibilities:
///   - hydrate <see cref="ProviderBytesTracker"/> from ProviderHourly so the
///     hot-path limit check has an accurate "bytes since reset" number after
///     restart or a config change,
///   - expose the same computation directly for read-only API consumers that
///     don't want to round-trip through the tracker.
///
/// The reset semantics are intentional: ResetAt is a unix-ms cutoff applied at
/// query time, not a DESTRUCTIVE delete of older rows. Historical graphs stay
/// intact across a reset; only the "bytes consumed against this block" gauge
/// rewinds. Offset is added on top so a user migrating from another client can
/// pre-seed "I've already burned 300 GB on this account" without faking events
/// into the metrics tables.
/// </summary>
public static class ProviderUsageHelper
{
    /// <summary>
    /// Fraction of the configured cap at which the provider is taken out of
    /// rotation. The headroom absorbs in-flight fetches that already passed
    /// the per-call check at <see cref="MultiProviderNntpClient"/> startup
    /// but haven't finished streaming bytes through CountingYencStream yet.
    /// 0.95 means "stop at 95% so the remaining 5% covers parallel fetches"
    /// — well above the worst realistic overshoot (MaxConnections × ~1 MB),
    /// and tiny compared to a typical multi-hundred-GB block.
    /// </summary>
    public const double EffectiveLimitFraction = 0.95;

    /// <summary>
    /// Computes raw bytes fetched for one provider since its last reset,
    /// summed from ProviderHourly. The caller adds <see cref="UsenetProviderConfig.ConnectionDetails.BytesUsedOffset"/>
    /// if it wants the user-facing total.
    /// </summary>
    public static async Task<long> ReadDbBytesSinceResetAsync(string host, long resetAt)
    {
        if (string.IsNullOrEmpty(host)) return 0;
        await using var db = new MetricsDbContext();
        // SumAsync over nothing returns 0; no need to guard for empty.
        return await db.ProviderHourly
            .Where(x => x.Provider == host && x.Hour >= resetAt)
            .SumAsync(x => x.BytesFetched)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Raw ProviderHourly rows over the last 7 days for the supplied hosts,
    /// grouped by host. Returning rows rather than a pre-aggregated sum lets
    /// the caller apply each provider's own ResetAt cutoff in memory without
    /// firing N queries — the settings page polls every 10s.
    /// </summary>
    public static async Task<Dictionary<string, List<(long Hour, long Bytes)>>> ReadRecentHoursAsync(
        IEnumerable<UsenetProviderConfig.ConnectionDetails> providers)
    {
        var providerList = providers.Where(p => !string.IsNullOrWhiteSpace(p.Id)).ToList();
        var legacyHostFallbackIds = GetLegacyHostFallbackProviderIds(providerList);
        var identities = providerList.Select(p => p.Id)
            .Concat(providerList
                .Where(p => legacyHostFallbackIds.Contains(p.Id))
                .Select(p => p.Host))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (identities.Length == 0) return new Dictionary<string, List<(long, long)>>();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long sevenDaysMs = 7L * 24 * 60 * 60 * 1000;
        var since = nowMs - sevenDaysMs;

        await using var db = new MetricsDbContext();
        var rows = await db.ProviderHourly
            .Where(x => identities.Contains(x.Provider) && x.Hour >= since)
            .Select(x => new { x.Provider, x.Hour, x.BytesFetched })
            .ToListAsync()
            .ConfigureAwait(false);

        return providerList.ToDictionary(
            p => p.Id,
            p => rows.Where(r => r.Provider == p.Id ||
                                 legacyHostFallbackIds.Contains(p.Id) && r.Provider == p.Host)
                .GroupBy(r => r.Hour)
                .Select(g => (g.Key, g.Sum(x => x.BytesFetched))).ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Per-provider burn rate (bytes/day) and projected days-until-cap,
    /// computed against the same time window as the live usage gauge.
    ///
    /// Window = [max(ResetAt, now − 7d), now]. This is the fix for the
    /// "0 used / 1h left" paradox: never fold pre-reset history into a
    /// post-reset projection. A freshly-reset counter shouldn't display
    /// a runout inherited from last week's downloads.
    ///
    /// Returns (rate, null) — honest rate, no projection — when any of:
    ///   - no cap configured,
    ///   - nothing used since reset (no signal to project from),
    ///   - window shorter than 1h (a few minutes of data inflates the rate
    ///     into nonsense like "1h left" after a tiny burst),
    ///   - already at/over the cap (nothing to extrapolate).
    /// </summary>
    public static (long BytesPerDay, double? DaysRemaining) ComputeBurnRate(
        UsenetProviderConfig.ConnectionDetails provider,
        long bytesUsed,
        List<(long Hour, long Bytes)>? recentHours)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long sevenDaysMs = 7L * 24 * 60 * 60 * 1000;
        const long oneHourMs = 60L * 60 * 1000;
        const double msPerDay = 86_400_000d;

        var effectiveStart = Math.Max(provider.BytesUsedResetAt, nowMs - sevenDaysMs);
        var windowMs = nowMs - effectiveStart;

        long bytesInWindow = 0;
        if (recentHours != null)
        {
            foreach (var (hour, bytes) in recentHours)
                if (hour >= effectiveStart) bytesInWindow += bytes;
        }

        var bytesPerDay = windowMs > 0
            ? (long)(bytesInWindow / (windowMs / msPerDay))
            : 0;

        var limit = provider.ByteLimit;
        if (!limit.HasValue || limit.Value <= 0) return (bytesPerDay, null);
        if (bytesUsed <= 0) return (bytesPerDay, null);
        if (windowMs < oneHourMs) return (bytesPerDay, null);
        if (bytesPerDay <= 0) return (bytesPerDay, null);
        var remaining = limit.Value - bytesUsed;
        if (remaining <= 0) return (bytesPerDay, null);
        return (bytesPerDay, (double)remaining / bytesPerDay);
    }

    /// <summary>
    /// Walks every provider in <paramref name="config"/> and writes its
    /// since-reset byte total into <paramref name="tracker"/>. Best-effort —
    /// failures are logged but never thrown, since a metrics DB hiccup must
    /// not prevent the streaming client from starting up.
    /// </summary>
    public static async Task SeedTrackerAsync(ProviderBytesTracker tracker, UsenetProviderConfig config)
    {
        if (config.Providers.Count == 0) return;
        try
        {
            await using var db = new MetricsDbContext();
            var legacyHostFallbackIds = GetLegacyHostFallbackProviderIds(config.Providers);
            foreach (var provider in config.Providers)
            {
                if (string.IsNullOrWhiteSpace(provider.Id)) continue;
                var includeLegacyHost = legacyHostFallbackIds.Contains(provider.Id);
                var bytes = await db.ProviderHourly
                    .Where(x => (x.Provider == provider.Id ||
                                 includeLegacyHost && x.Provider == provider.Host)
                                && x.Hour >= provider.BytesUsedResetAt)
                    .SumAsync(x => x.BytesFetched)
                    .ConfigureAwait(false);
                tracker.SetLifetime(provider.Id, bytes);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to seed ProviderBytesTracker from metrics DB; continuing with zeros.");
        }
    }

    /// <summary>
    /// Identifies providers for which old host-keyed metrics can be attributed
    /// unambiguously. When multiple accounts use the same NNTP host, assigning
    /// the shared legacy total to every account duplicates usage and can pause
    /// otherwise unused block accounts. New metrics are keyed by provider ID,
    /// so shared hosts must use ID-only accounting.
    /// </summary>
    internal static HashSet<string> GetLegacyHostFallbackProviderIds(
        IEnumerable<UsenetProviderConfig.ConnectionDetails> providers)
    {
        return providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Host))
            .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single().Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Total user-facing usage = bytes since reset (live in tracker) + offset.
    /// Clamped to 0 so a negative offset (manual correction) never reports as
    /// a negative gauge value.
    /// </summary>
    public static long ComputeUsage(ProviderBytesTracker tracker, UsenetProviderConfig.ConnectionDetails provider)
    {
        var live = tracker.GetLifetime(provider.Id);
        return Math.Max(0, live + provider.BytesUsedOffset);
    }

    /// <summary>
    /// True when a configured ByteLimit exists and the live counter has caught
    /// up to or passed the effective cutoff (configured limit × safety margin).
    /// A ByteLimit of null or 0 means "no cap".
    /// </summary>
    public static bool IsOverLimit(ProviderBytesTracker tracker, UsenetProviderConfig.ConnectionDetails provider)
    {
        var limit = provider.ByteLimit;
        if (!limit.HasValue || limit.Value <= 0) return false;
        var effective = (long)(limit.Value * EffectiveLimitFraction);
        return ComputeUsage(tracker, provider) >= effective;
    }
}
