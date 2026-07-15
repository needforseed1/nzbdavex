namespace NzbWebDAV.Clients.Usenet;

public interface IQueueConnectionWarmer
{
    Task PrewarmQueueAsync(int targetConnections, CancellationToken cancellationToken);
    Task PrewarmHealthCheckAsync(CancellationToken cancellationToken);
    Task PrewarmPrimaryHealthCheckAsync(CancellationToken cancellationToken);
    Task PrimeHealthCheckAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken);
    Task PrimePrimaryHealthCheckAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken);
}
