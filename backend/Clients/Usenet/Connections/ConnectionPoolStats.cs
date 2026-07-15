using System.Diagnostics;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

public class ConnectionPoolStats
{
    private readonly int[] _live;
    private readonly int[] _idle;
    private readonly int[] _pending;
    private readonly int[] _warm;
    private readonly int[] _effectiveMax;
    private readonly int _max;
    private int _totalLive;
    private int _totalIdle;
    private int _totalWarm;
    private long? _zeroActiveStartedAt;
    private string? _zeroActiveSnapshot;
    private readonly UsenetProviderConfig _providerConfig;
    private readonly WebsocketManager _websocketManager;
    private readonly Action<Action> _broadcastDebounced;
    private readonly SemaphoreSlim _broadcastGate = new(1, 1);

    public ConnectionPoolStats(UsenetProviderConfig providerConfig, WebsocketManager websocketManager)
    {
        var count = providerConfig.Providers.Count;
        _live = new int[count];
        _idle = new int[count];
        _pending = new int[count];
        _warm = new int[count];
        _effectiveMax = providerConfig.Providers.Select(x => x.MaxConnections).ToArray();
        _max = providerConfig.Providers
            .Where(x => IsVisiblePoolType(x.Type))
            .Select(x => x.MaxConnections)
            .Sum();

        _providerConfig = providerConfig;
        _websocketManager = websocketManager;
        _broadcastDebounced = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(50));
    }

    public EventHandler<ConnectionPoolChangedEventArgs> GetOnConnectionPoolChanged(int providerIndex)
    {
        return OnEvent;

        void OnEvent(object? _, ConnectionPoolChangedEventArgs args)
        {
            string? transitionMessage = null;
            if (IsVisiblePoolType(_providerConfig.Providers[providerIndex].Type))
            {
                lock (this)
                {
                    _live[providerIndex] = args.Live;
                    _idle[providerIndex] = args.Idle;
                    _pending[providerIndex] = args.Pending;
                    _warm[providerIndex] = args.Warm;
                    _effectiveMax[providerIndex] = args.EffectiveMax;
                    _totalLive = _live.Sum();
                    _totalIdle = _idle.Sum();
                    _totalWarm = _warm.Sum();
                    var active = _totalLive - _totalIdle;
                    var pending = _pending.Sum();
                    if (active == 0 && pending > 0 && !_zeroActiveStartedAt.HasValue)
                    {
                        _zeroActiveStartedAt = Stopwatch.GetTimestamp();
                        _zeroActiveSnapshot = FormatProviderSnapshot();
                    }
                    else if (active > 0 && _zeroActiveStartedAt.HasValue)
                    {
                        var zeroFor = Stopwatch.GetElapsedTime(_zeroActiveStartedAt.Value);
                        if (zeroFor >= TimeSpan.FromMilliseconds(50) &&
                            zeroFor <= TimeSpan.FromSeconds(30))
                            transitionMessage =
                                $"Usenet active connections resumed after {zeroFor.TotalMilliseconds:F0}ms at zero; " +
                                $"zeroState=[{_zeroActiveSnapshot}]; resumedState=[{FormatProviderSnapshot()}].";
                        _zeroActiveStartedAt = null;
                        _zeroActiveSnapshot = null;
                    }
                    else if (active == 0 && pending == 0)
                    {
                        _zeroActiveStartedAt = null;
                        _zeroActiveSnapshot = null;
                    }
                }
            }

            if (transitionMessage is not null) Log.Debug(transitionMessage);
            _broadcastDebounced(() => _ = BroadcastSnapshotAsync());
        }
    }

    private string FormatProviderSnapshot()
    {
        return string.Join("; ", _providerConfig.Providers
            .Select((provider, index) => new { provider, index })
            .Where(x => IsVisiblePoolType(x.provider.Type))
            .Select(x =>
            {
                var name = string.IsNullOrWhiteSpace(x.provider.Nickname)
                    ? x.provider.Host
                    : x.provider.Nickname;
                var active = _live[x.index] - _idle[x.index];
                return $"{name} live={_live[x.index]} idle={_idle[x.index]} active={active} " +
                       $"pending={_pending[x.index]} cap={_effectiveMax[x.index]}/{x.provider.MaxConnections}";
            }));
    }

    private async Task BroadcastSnapshotAsync()
    {
        await _broadcastGate.WaitAsync().ConfigureAwait(false);
        try
        {
            int[] live;
            int[] idle;
            int totalLive;
            int totalIdle;
            int totalWarm;
            lock (this)
            {
                live = [.._live];
                idle = [.._idle];
                totalLive = _totalLive;
                totalIdle = _totalIdle;
                totalWarm = _totalWarm;
            }

            for (var providerIndex = 0; providerIndex < _providerConfig.Providers.Count; providerIndex++)
            {
                if (!IsVisiblePoolType(_providerConfig.Providers[providerIndex].Type)) continue;
                var providerId = _providerConfig.Providers[providerIndex].Id;
                var message =
                    $"{providerId}|{live[providerIndex]}|{idle[providerIndex]}|{totalLive}|{_max}|{totalIdle}|{totalWarm}";
                await _websocketManager.SendMessage(WebsocketTopic.UsenetConnections, message)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    private static bool IsVisiblePoolType(ProviderType type) =>
        type is ProviderType.Pooled
            or ProviderType.BackupAndStats
            or ProviderType.HealthChecksOnly;

    public sealed class ConnectionPoolChangedEventArgs(
        int live, int idle, int max, int pending = 0, int? effectiveMax = null,
        int? warm = null) : EventArgs
    {
        public int Live { get; } = live;
        public int Idle { get; } = idle;
        public int Max { get; } = max;
        public int Pending { get; } = pending;
        public int EffectiveMax { get; } = effectiveMax ?? max;
        public int Warm { get; } = warm ?? live;
        public int Active => Live - Idle;
    }
}
