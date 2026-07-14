using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Clients.Usenet;

public class UsenetStreamingClient : WrappingNntpClient
{
    internal const bool HealthProviderPrewarmEnabled = true;
    private const int WarmConnectionsPerProvider = 16;
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WarmConnectionRefreshInterval = TimeSpan.FromSeconds(15);

    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker)
        : base(CreateDownloadingNntpClient(configManager, websocketManager, usageTracker, metricsWriter, bytesTracker))
    {
        // when config changes, create a new MultiProviderClient to use instead.
        configManager.OnConfigChanged += (_, configEventArgs) =>
        {
            // if unrelated config changed, do nothing
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            // update the connection-pool according to the new config
            var newUsenetClient = CreateDownloadingNntpClient(configManager, websocketManager, usageTracker, metricsWriter, bytesTracker);
            ReplaceUnderlyingClient(newUsenetClient);
        };
    }

    private static INntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker
    )
    {
        var multiProviderClient = CreateMultiProviderClient(configManager, websocketManager, usageTracker, metricsWriter, bytesTracker);
        var downloadingClient = new DownloadingNntpClient(multiProviderClient, configManager);
        if (!configManager.IsSegmentCacheEnabled()) return downloadingClient;
        try
        {
            return new SegmentCacheNntpClient(
                downloadingClient,
                configManager.GetSegmentCachePath(),
                configManager.GetSegmentCacheMaxBytes()
            );
        }
        catch (Exception e)
        {
            Log.Warning(e, "Segment cache disabled: failed to initialise at {Path}.",
                configManager.GetSegmentCachePath());
            return downloadingClient;
        }
    }

    private static MultiProviderNntpClient CreateMultiProviderClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker
    )
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        // Seed the tracker before publishing any provider pools. Otherwise a
        // block account can briefly fetch as uncapped during startup/reload.
        // The helper logs and swallows metrics DB errors, so a metrics outage
        // still cannot prevent the client from starting.
        ProviderUsageHelper.SeedTrackerAsync(bytesTracker, providerConfig)
            .GetAwaiter().GetResult();

        var connectionPoolStats = new ConnectionPoolStats(providerConfig, websocketManager);
        var providerClients = providerConfig.Providers
            .Select((provider, index) => CreateProviderClient(
                provider,
                connectionPoolStats.GetOnConnectionPoolChanged(index)
            ))
            .ToList();
        return new MultiProviderNntpClient(providerClients, usageTracker, metricsWriter, bytesTracker,
            cascadeEnabled: configManager.IsCascadeEnabled);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var keepWarm = GetWarmConnectionTarget(connectionDetails.Type, connectionDetails.MaxConnections);
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            minimumIdleConnections: keepWarm,
            providerHost: connectionDetails.Host,
            onConnectionPoolChanged: onConnectionPoolChanged
        );
        if (keepWarm > 0)
            _ = PrewarmConnectionPoolAsync(connectionPool, keepWarm, connectionDetails.Host);
        var circuitBreaker = new ProviderCircuitBreaker(connectionDetails.Host);
        return new MultiConnectionNntpClient(
            connectionPool,
            connectionDetails.Type,
            circuitBreaker,
            connectionDetails.Host,
            connectionDetails.ByteLimit,
            connectionDetails.BytesUsedOffset,
            connectionDetails.Priority,
            connectionDetails.PrepOnly,
            connectionDetails.PrepSpreadEnabled,
            connectionDetails.PipeliningDepth,
            connectionDetails.HealthPipeliningDepth,
            connectionDetails.Id
        );
    }

    internal static int GetWarmConnectionTarget(ProviderType providerType, int maxConnections)
    {
        if (maxConnections <= 0) return 0;

        // Providers participating explicitly in bulk health checks benefit from
        // having their full allowance authenticated before a job arrives. Idle
        // sessions cost no article quota; backup+STAT providers may still serve
        // BODY/ARTICLE later when normal failover requires them.
        if (providerType is ProviderType.HealthChecksOnly or ProviderType.BackupAndStats)
            return HealthProviderPrewarmEnabled ? maxConnections : 0;

        return providerType == ProviderType.Pooled
            ? Math.Min(maxConnections, WarmConnectionsPerProvider)
            : 0;
    }

    private static ConnectionPool<INntpClient> CreateNewConnectionPool
    (
        int maxConnections,
        Func<CancellationToken, ValueTask<INntpClient>> connectionFactory,
        int minimumIdleConnections,
        string providerHost,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = new ConnectionPool<INntpClient>(
            maxConnections,
            connectionFactory,
            idleTimeout: ConnectionIdleTimeout,
            connectionValidator: ValidateConnection,
            minimumIdleConnections: minimumIdleConnections,
            warmConnectionRefreshInterval: minimumIdleConnections > 0
                ? WarmConnectionRefreshInterval
                : null,
            connectionCapacityRejected: IsConnectionCapacityRejected,
            onConnectionCapacityReduced: (effectiveMax, _) => Log.Warning(
                "NNTP provider {Provider} rejected additional connections; limiting this pool to {EffectiveMax} " +
                "accepted connections until provider settings are reloaded.", providerHost, effectiveMax));
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    private static bool IsConnectionCapacityRejected(Exception exception)
    {
        return exception.TryGetCausingException(out UsenetConnectionLimitException _);
    }

    private static async Task PrewarmConnectionPoolAsync(
        ConnectionPool<INntpClient> connectionPool,
        int count,
        string host)
    {
        try
        {
            await connectionPool.PrewarmAsync(count, SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
            Log.Information("Prewarmed {Count} NNTP connections for {Provider}.", count, host);
        }
        catch (OperationCanceledException) when (SigtermUtil.GetCancellationToken().IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not prewarm NNTP connections for {Provider}.", host);
        }
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    )
    {
        var connection = new BaseNntpClient();
        try
        {
            var host = connectionDetails.Host;
            var port = connectionDetails.Port;
            var useSsl = connectionDetails.UseSsl;
            var user = connectionDetails.User;
            var pass = connectionDetails.Pass;
            await connection.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);
            await connection.AuthenticateAsync(user, pass, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async ValueTask<bool> ValidateConnection(INntpClient connection, CancellationToken ct)
    {
        using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        validationCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await connection.DateAsync(validationCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
