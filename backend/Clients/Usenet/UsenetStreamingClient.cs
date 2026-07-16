using System.Security.Cryptography;
using System.Text.Json;
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
    internal const int ApplicationConnectionLimit = 512;
    internal const int ConcurrentConnectionAttemptLimit = 32;
    internal static readonly TimeSpan ConnectionSetupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WarmConnectionRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly ConnectionLifetimeBudget ConnectionBudget = new(
        ApplicationConnectionLimit, ConcurrentConnectionAttemptLimit);
    private readonly DrainingNntpClient _drainingClient;
    private readonly object _reloadLock = new();
    private readonly ConfigManager _configManager;
    private readonly EventHandler<ConfigManager.ConfigEventArgs> _configChangedHandler;
    private string _providerConfigFingerprint;
    private bool _disposed;

    public UsenetStreamingClient(
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker)
        : this(
            new DrainingNntpClient(CreateDownloadingNntpClient(
                configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
                activateIdlePrewarming: true)),
            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker)
    {
    }

    private UsenetStreamingClient(
        DrainingNntpClient drainingClient,
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker)
        : base(drainingClient)
    {
        _drainingClient = drainingClient;
        _configManager = configManager;
        _providerConfigFingerprint = GetProviderConfigFingerprint(configManager);
        _configChangedHandler = (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.ContainsKey("usenet.providers")) return;

            // Config updates can arrive concurrently. Serializing the read/build/swap
            // sequence prevents a slower older build from publishing after a newer one.
            lock (_reloadLock)
            {
                if (_disposed) return;
                var nextFingerprint = GetProviderConfigFingerprint(configManager);
                if (string.Equals(_providerConfigFingerprint, nextFingerprint, StringComparison.Ordinal)) return;
                var newUsenetClient = CreateDownloadingNntpClient(
                    configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
                    activateIdlePrewarming: false);
                _drainingClient.Replace(newUsenetClient);
                _providerConfigFingerprint = nextFingerprint;
            }
        };
        _configManager.OnConfigChanged += _configChangedHandler;
    }

    internal static string GetProviderConfigFingerprint(ConfigManager configManager)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(configManager.GetUsenetProviderConfig());
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    public override void Dispose()
    {
        lock (_reloadLock)
        {
            if (_disposed) return;
            _disposed = true;
            _configManager.OnConfigChanged -= _configChangedHandler;
            base.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static INntpClient CreateDownloadingNntpClient
    (
        ConfigManager configManager,
        WebsocketManager websocketManager,
        ProviderUsageTracker usageTracker,
        MetricsWriter metricsWriter,
        ProviderBytesTracker bytesTracker,
        bool activateIdlePrewarming
    )
    {
        var multiProviderClient = CreateMultiProviderClient(
            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker);
        if (activateIdlePrewarming)
            multiProviderClient.ActivateIdlePrewarming();
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
            cascadeEnabled: configManager.IsCascadeEnabled,
            warmValidationConnectionBudget: configManager.GetWarmValidationConnectionBudget);
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

        // Keep 90% of each health-capable provider authenticated. Round up so
        // small pools retain at least the requested proportion while leaving
        // capacity for on-demand growth whenever the configured limit allows it.
        if (providerType is ProviderType.HealthChecksOnly or ProviderType.BackupAndStats)
            return HealthProviderPrewarmEnabled ? maxConnections - maxConnections / 10 : 0;

        // Primary providers serve prep, playback, and health checks. Keeping half
        // their pool authenticated avoids a cold prep-to-STAT transition while
        // leaving the remaining capacity available for demand-driven growth.
        return providerType == ProviderType.Pooled
            ? (maxConnections + 1) / 2
            : 0;
    }

    internal static int GetHealthCheckWarmConnectionTarget(
        ProviderType providerType,
        int maxConnections)
    {
        if (maxConnections <= 0) return 0;
        return providerType is ProviderType.Pooled
            or ProviderType.BackupAndStats
            or ProviderType.HealthChecksOnly
            ? maxConnections - maxConnections / 10
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
                "accepted connections until provider settings are reloaded.", providerHost, effectiveMax),
            connectionBudget: ConnectionBudget);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    private static bool IsConnectionCapacityRejected(Exception exception)
    {
        return exception.TryGetCausingException(out UsenetConnectionLimitException _);
    }

    public static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        CancellationToken ct
    ) => await CreateNewConnection(
        connectionDetails, ConnectionSetupTimeout, ct).ConfigureAwait(false);

    internal static async ValueTask<INntpClient> CreateNewConnection
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        TimeSpan setupTimeout,
        CancellationToken ct
    )
    {
        if (setupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(setupTimeout));

        var connection = new BaseNntpClient();
        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        setupCts.CancelAfter(setupTimeout);
        try
        {
            var host = connectionDetails.Host;
            var port = connectionDetails.Port;
            var useSsl = connectionDetails.UseSsl;
            var user = connectionDetails.User;
            var pass = connectionDetails.Pass;
            await connection.ConnectAsync(host, port, useSsl, setupCts.Token).ConfigureAwait(false);
            await connection.AuthenticateAsync(user, pass, setupCts.Token).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException e) when (
            setupCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            connection.Dispose();
            throw new TimeoutException(
                $"NNTP connection setup timed out after {setupTimeout.TotalSeconds:0.#} seconds.", e);
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
