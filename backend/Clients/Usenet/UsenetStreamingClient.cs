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
    internal const string PersistentIdleFloorTestEnvironmentVariable =
        "NZBDAV_TEST_PERSISTENT_IDLE_FLOOR";
    internal const string PersistentWarmFloorTestEnvironmentVariable =
        "NZBDAV_TEST_PERSISTENT_WARM_FLOOR";
    internal const string PersistentPrimaryReadyFloorTestEnvironmentVariable =
        "NZBDAV_TEST_PERSISTENT_PRIMARY_READY_FLOOR";
    internal const string PersistentHealthReadyFloorTestEnvironmentVariable =
        "NZBDAV_TEST_PERSISTENT_HEALTH_READY_FLOOR";
    internal const string WarmValidationWindowTestEnvironmentVariable =
        "NZBDAV_TEST_WARM_VALID_SECONDS";
    internal const string WarmRefreshIntervalTestEnvironmentVariable =
        "NZBDAV_TEST_WARM_REFRESH_SECONDS";
    private static readonly HashSet<string> PoolConfigurationKeys =
    [
        "usenet.providers",
        "usenet.ready-connections.primary",
        "usenet.ready-connections.health",
    ];
    internal const bool HealthProviderPrewarmEnabled = true;
    internal const int ApplicationConnectionLimit = 512;
    internal const int ConcurrentConnectionAttemptLimit = 32;
    internal static readonly TimeSpan ConnectionSetupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly WarmConnectionTimings DefaultWarmConnectionTimings =
        new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30));
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
        _providerConfigFingerprint = GetPoolConfigFingerprint(configManager);
        _configChangedHandler = (_, configEventArgs) =>
        {
            if (!configEventArgs.ChangedConfig.Keys.Any(PoolConfigurationKeys.Contains)) return;

            // Config updates can arrive concurrently. Serializing the read/build/swap
            // sequence prevents a slower older build from publishing after a newer one.
            lock (_reloadLock)
            {
                if (_disposed) return;
                var nextFingerprint = GetPoolConfigFingerprint(configManager);
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

    internal static string GetPoolConfigFingerprint(ConfigManager configManager)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Providers = configManager.GetUsenetProviderConfig(),
            PrimaryReadyConnections = configManager.GetPrimaryReadyConnections(),
            HealthReadyConnections = configManager.GetHealthReadyConnections(),
        });
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
        var persistentIdleFloorOverride = GetPersistentIdleFloorTestOverride();
        var persistentWarmFloorOverride = GetPersistentWarmFloorTestOverride();
        var roleReadyFloorTestOverrides = new PersistentReadyFloorTargets(
            GetNonNegativeTestOverride(
                PersistentPrimaryReadyFloorTestEnvironmentVariable),
            GetNonNegativeTestOverride(
                PersistentHealthReadyFloorTestEnvironmentVariable));
        var roleReadyFloorTargets = new PersistentReadyFloorTargets(
            roleReadyFloorTestOverrides.Primary
                ?? configManager.GetPrimaryReadyConnections(),
            roleReadyFloorTestOverrides.BackupHealth
                ?? configManager.GetHealthReadyConnections());
        var validationWindowOverride =
            GetPositiveTestOverride(WarmValidationWindowTestEnvironmentVariable);
        var refreshIntervalOverride =
            GetPositiveTestOverride(WarmRefreshIntervalTestEnvironmentVariable);
        var warmConnectionTimings = ResolveWarmConnectionTimings(
            validationWindowOverride,
            refreshIntervalOverride);
        if (persistentIdleFloorOverride.HasValue || persistentWarmFloorOverride.HasValue)
            Log.Warning(
                "NNTP persistent-floor test mode is active: idleFloor={IdleFloor}, " +
                "warmFloor={WarmFloor} per eligible provider. " +
                "Queue and health-check burst targets are unchanged.",
                persistentIdleFloorOverride,
                persistentWarmFloorOverride);
        if (roleReadyFloorTestOverrides.IsConfigured)
            Log.Warning(
                "NNTP role-ready-floor test mode is active: primaryFloor={PrimaryFloor}, " +
                "backupHealthFloor={BackupHealthFloor}. Plain backup providers remain " +
                "recovery-only. Queue and health-check burst targets are unchanged.",
                roleReadyFloorTestOverrides.Primary,
                roleReadyFloorTestOverrides.BackupHealth);
        if (validationWindowOverride.HasValue || refreshIntervalOverride.HasValue)
            Log.Warning(
                "NNTP warm-timing test mode is active: validationWindow={ValidationWindow}s, " +
                "refreshInterval={RefreshInterval}s.",
                warmConnectionTimings.ValidationWindow.TotalSeconds,
                warmConnectionTimings.RefreshInterval.TotalSeconds);
        var multiProviderClient = CreateMultiProviderClient(
            configManager, websocketManager, usageTracker, metricsWriter, bytesTracker,
            persistentIdleFloorOverride, persistentWarmFloorOverride,
            roleReadyFloorTargets,
            warmConnectionTimings);
        if (ShouldActivateIdlePrewarming(
                activateIdlePrewarming,
                persistentIdleFloorOverride,
                roleReadyFloorTargets.IsConfigured))
            multiProviderClient.ActivateIdlePrewarming();
        var downloadingClient = new DownloadingNntpClient(
            multiProviderClient,
            configManager,
            multiProviderClient.UpdateConnectionPriorityOdds);
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
        ProviderBytesTracker bytesTracker,
        int? persistentIdleFloorOverride,
        int? persistentWarmFloorOverride,
        PersistentReadyFloorTargets roleReadyFloorTargets,
        WarmConnectionTimings warmConnectionTimings
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
                connectionPoolStats.GetOnConnectionPoolChanged(index),
                persistentIdleFloorOverride,
                persistentWarmFloorOverride,
                roleReadyFloorTargets,
                warmConnectionTimings
            ))
            .ToList();
        return new MultiProviderNntpClient(providerClients, usageTracker, metricsWriter, bytesTracker,
            cascadeEnabled: configManager.IsCascadeEnabled,
            warmValidationConnectionBudget: configManager.GetWarmValidationConnectionBudget,
            connectionBudget: ConnectionBudget);
    }

    private static MultiConnectionNntpClient CreateProviderClient
    (
        UsenetProviderConfig.ConnectionDetails connectionDetails,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged,
        int? persistentIdleFloorOverride,
        int? persistentWarmFloorOverride,
        PersistentReadyFloorTargets roleReadyFloorTargets,
        WarmConnectionTimings warmConnectionTimings
    )
    {
        var roleReadyTarget = GetRoleReadyConnectionTarget(
            connectionDetails.Type,
            connectionDetails.MaxConnections,
            roleReadyFloorTargets);
        var keepWarm = persistentIdleFloorOverride.HasValue
            ? GetPersistentIdleConnectionTarget(
                connectionDetails.Type,
                connectionDetails.MaxConnections,
                persistentIdleFloorOverride)
            : roleReadyTarget ?? GetPersistentIdleConnectionTarget(
                connectionDetails.Type,
                connectionDetails.MaxConnections,
                testOverride: null);
        var keepValidated = persistentWarmFloorOverride.HasValue
            ? GetPersistentWarmConnectionTarget(keepWarm, persistentWarmFloorOverride)
            : Math.Min(
                roleReadyTarget ?? GetPersistentWarmConnectionTarget(keepWarm, testOverride: null),
                keepWarm);
        var connectionPool = CreateNewConnectionPool(
            maxConnections: connectionDetails.MaxConnections,
            connectionFactory: ct => CreateNewConnection(connectionDetails, ct),
            minimumIdleConnections: keepWarm,
            minimumWarmConnections: keepValidated,
            warmConnectionTimings: warmConnectionTimings,
            useRecoveryCapacity: connectionDetails.Type == ProviderType.BackupOnly,
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

    private static int? GetPersistentIdleFloorTestOverride()
    {
        return GetNonNegativeTestOverride(PersistentIdleFloorTestEnvironmentVariable);
    }

    private static int? GetPersistentWarmFloorTestOverride()
    {
        return GetNonNegativeTestOverride(PersistentWarmFloorTestEnvironmentVariable);
    }

    private static int? GetNonNegativeTestOverride(string environmentVariable)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!int.TryParse(raw, out var floor) || floor < 0)
        {
            Log.Warning(
                "Ignoring invalid {EnvironmentVariable} value. Expected a non-negative integer.",
                environmentVariable);
            return null;
        }

        return floor;
    }

    private static int? GetPositiveTestOverride(string environmentVariable)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!int.TryParse(raw, out var seconds) || seconds <= 0)
        {
            Log.Warning(
                "Ignoring invalid {EnvironmentVariable} value. Expected a positive integer.",
                environmentVariable);
            return null;
        }

        return seconds;
    }

    internal static WarmConnectionTimings ResolveWarmConnectionTimings(
        int? validationWindowSeconds,
        int? refreshIntervalSeconds)
    {
        var validationWindow = validationWindowSeconds.HasValue
            ? TimeSpan.FromSeconds(validationWindowSeconds.Value)
            : DefaultWarmConnectionTimings.ValidationWindow;
        var refreshInterval = refreshIntervalSeconds.HasValue
            ? TimeSpan.FromSeconds(refreshIntervalSeconds.Value)
            : DefaultWarmConnectionTimings.RefreshInterval;

        if (validationWindow <= TimeSpan.Zero ||
            refreshInterval <= TimeSpan.Zero ||
            refreshInterval >= validationWindow)
        {
            Log.Warning(
                "Ignoring invalid NNTP warm timing overrides. Refresh must be shorter than " +
                "the validation window; using {ValidationWindow}s/{RefreshInterval}s.",
                DefaultWarmConnectionTimings.ValidationWindow.TotalSeconds,
                DefaultWarmConnectionTimings.RefreshInterval.TotalSeconds);
            return DefaultWarmConnectionTimings;
        }

        return new WarmConnectionTimings(validationWindow, refreshInterval);
    }

    internal static int GetPersistentIdleConnectionTarget(
        ProviderType providerType,
        int maxConnections,
        int? testOverride)
    {
        if (maxConnections <= 0 ||
            providerType is ProviderType.BackupOnly or ProviderType.Disabled)
            return 0;

        return testOverride.HasValue
            ? Math.Min(testOverride.Value, maxConnections)
            : GetWarmConnectionTarget(providerType, maxConnections);
    }

    internal static int GetPersistentWarmConnectionTarget(
        int persistentIdleConnectionTarget,
        int? testOverride)
    {
        if (persistentIdleConnectionTarget <= 0) return 0;
        return Math.Min(
            testOverride ?? 4,
            persistentIdleConnectionTarget);
    }

    internal static bool ShouldActivateIdlePrewarming(
        bool requestedByLifecycle,
        int? persistentIdleFloorTestOverride,
        bool roleReadyFloorTestOverride = false) =>
        requestedByLifecycle ||
        persistentIdleFloorTestOverride.HasValue ||
        roleReadyFloorTestOverride;

    internal static int? GetRoleReadyConnectionTarget(
        ProviderType providerType,
        int maxConnections,
        PersistentReadyFloorTargets targets)
    {
        if (maxConnections <= 0) return 0;

        var requested = providerType switch
        {
            ProviderType.Pooled => targets.Primary,
            ProviderType.HealthChecksOnly or ProviderType.BackupAndStats =>
                targets.BackupHealth,
            _ => null,
        };
        return requested.HasValue
            ? Math.Min(requested.Value, maxConnections)
            : null;
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
        int minimumWarmConnections,
        WarmConnectionTimings warmConnectionTimings,
        bool useRecoveryCapacity,
        string providerHost,
        EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs> onConnectionPoolChanged
    )
    {
        var connectionPool = new ConnectionPool<INntpClient>(
            maxConnections,
            connectionFactory,
            idleTimeout: ConnectionIdleTimeout,
            connectionValidator: ValidateConnection,
            validateAfterIdle: warmConnectionTimings.ValidationWindow,
            minimumIdleConnections: minimumIdleConnections,
            minimumWarmConnections: minimumWarmConnections,
            warmConnectionRefreshInterval: minimumIdleConnections > 0
                ? warmConnectionTimings.RefreshInterval
                : null,
            connectionCapacityRejected: IsConnectionCapacityRejected,
            onConnectionCapacityReduced: (effectiveMax, _) => Log.Warning(
                "NNTP provider {Provider} rejected additional connections; limiting this pool to {EffectiveMax} " +
                "accepted connections until provider settings are reloaded.", providerHost, effectiveMax),
            onWarmFloorStateChanged: (warm, target, ready) =>
            {
                if (ready)
                    Log.Information(
                        "NNTP provider {Provider} restored its ready connection floor ({Warm}/{Target}).",
                        providerHost, warm, target);
                else
                    Log.Warning(
                        "NNTP provider {Provider} has remained below its ready connection floor " +
                        "for 30s ({Warm}/{Target}). Background maintenance will keep retrying.",
                        providerHost, warm, target);
            },
            connectionBudget: ConnectionBudget,
            useRecoveryCapacity: useRecoveryCapacity);
        connectionPool.OnConnectionPoolChanged += onConnectionPoolChanged;
        var args = new ConnectionPoolStats.ConnectionPoolChangedEventArgs(0, 0, maxConnections);
        onConnectionPoolChanged(connectionPool, args);
        return connectionPool;
    }

    internal readonly record struct WarmConnectionTimings(
        TimeSpan ValidationWindow,
        TimeSpan RefreshInterval);

    internal readonly record struct PersistentReadyFloorTargets(
        int? Primary,
        int? BackupHealth)
    {
        public bool IsConfigured =>
            Primary.HasValue || BackupHealth.HasValue;
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
