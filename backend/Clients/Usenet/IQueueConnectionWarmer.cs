namespace NzbWebDAV.Clients.Usenet;

public sealed class QueueHealthQualification
{
    public static QueueHealthQualification None { get; } = new(null);

    internal object? State { get; }

    internal QueueHealthQualification(object? state)
    {
        State = state;
    }
}

public interface IQueueConnectionWarmer
{
    Task PrewarmQueueAsync(int targetConnections, CancellationToken cancellationToken);
    Task PrewarmHealthCheckAsync(int connectionDemand, CancellationToken cancellationToken);
    Task PrewarmPrimaryHealthCheckAsync(int connectionDemand, CancellationToken cancellationToken);
    Task PrimeHealthCheckAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken);
    Task PrimePrimaryHealthCheckAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken);
    Task<QueueHealthQualification> QualifyHealthCheckAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        CancellationToken cancellationToken);
    Task CheckAllSegmentsPipelinedAfterQualificationAsync(
        QueueHealthQualification qualification,
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken);
}
