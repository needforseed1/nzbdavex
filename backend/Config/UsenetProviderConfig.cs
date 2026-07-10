using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    public int TotalStatCheckConnections => Math.Max(1, Providers
        .Where(x => x.Type is ProviderType.Pooled
            or ProviderType.BackupAndStats
            or ProviderType.HealthChecksOnly)
        .Select(x => x.MaxConnections)
        .Sum());

    public class ConnectionDetails
    {
        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }

        public int Priority { get; set; }

        public int? PipeliningDepth { get; set; }

        // Optional STAT-only override. Kept separate because health checks can
        // safely use much deeper pipelines than BODY/ARTICLE traffic.
        public int? HealthPipeliningDepth { get; set; }

        // When true, this provider can be used for preparation work such as
        // imports, first-segment checks, fast verification and health/preflight
        // checks, but is excluded from high-priority playback streams.
        public bool PrepOnly { get; set; }

        // When global prep spreading is enabled, pooled providers with this
        // flag participate in the first-choice queue/import/preflight spread.
        // Set false to keep a pooled provider mostly idle unless others miss
        // or fail. Backup providers are not first-choice spread targets outside
        // the dedicated BackupAndStats health-check path.
        public bool PrepSpreadEnabled { get; set; } = true;

        // Optional user-friendly label shown in the UI in place of Host. Host is
        // still the real NNTP target and the stable key used for metrics/logs.
        public string? Nickname { get; set; }

        // null or 0 = no cap. Used by block-account holders to stop a paid block
        // from being drained beyond its purchased size.
        public long? ByteLimit { get; set; }

        // bytes added to the computed usage. Lets the user seed a starting value
        // when migrating from another client, or adjust drift against the
        // provider's own portal. Set to 0 after a reset.
        public long BytesUsedOffset { get; set; }

        // unix-ms cutoff: ProviderHourly rows older than this don't contribute to
        // the live counter. A reset bumps this to "now" so the gauge starts fresh
        // without losing the historical metrics rows underneath.
        public long BytesUsedResetAt { get; set; }
    }
}
