namespace NzbWebDAV.Clients.Usenet;

public interface IQueueConnectionWarmer
{
    Task PrewarmQueueAsync(int targetConnections, CancellationToken cancellationToken);
}
