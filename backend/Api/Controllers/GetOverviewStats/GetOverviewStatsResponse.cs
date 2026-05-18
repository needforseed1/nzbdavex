namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

public class GetOverviewStatsResponse
{
    public string Window { get; init; } = "24h";
    public LiveTiles Tiles { get; init; } = new();
    public List<ThroughputPoint> Throughput { get; init; } = new();
    public double ReadAmplification { get; init; }
    public List<ProviderRow> Providers { get; init; } = new();
    public CatalogueBlock Catalogue { get; init; } = new();
    public RepairBlock Repair { get; init; } = new();

    public class LiveTiles
    {
        public int ActiveReads { get; init; }
        public long ArticlesPerMinute { get; init; }
        public long ErrorsPerMinute { get; init; }
        public long BytesServedPerMinute { get; init; }
    }

    public class ThroughputPoint
    {
        public long Bucket { get; init; }
        public long BytesServed { get; init; }
        public long BytesFetched { get; init; }
        public long Articles { get; init; }
        public long Errors { get; init; }
    }

    public class ProviderRow
    {
        public string Provider { get; init; } = "";
        public long Articles { get; init; }
        public long BytesFetched { get; init; }
        public long Errors { get; init; }
        public long Retries { get; init; }
        public double AvgDurationMs { get; init; }
        public double ErrorRate { get; init; }
    }

    public class CatalogueBlock
    {
        public long FileCount { get; init; }
        public long TotalBytes { get; init; }
        public double CheckedPercent { get; init; }
        public long RepairBacklog { get; init; }
    }

    public class RepairBlock
    {
        public long Healthy { get; init; }
        public long Repaired { get; init; }
        public long Deleted { get; init; }
        public long ActionNeeded { get; init; }
    }
}
