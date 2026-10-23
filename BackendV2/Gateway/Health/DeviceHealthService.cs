using System.Collections.Concurrent;
using System.Text.Json;
using BackendV2.Data;
using BackendV2.Gateway.Alerts;
using BackendV2.Gateway.Mqtt;
using BackendV2.Gateway.Realtime;
using BackendV2.Gateway.Uplink;
using BackendV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace BackendV2.Gateway.Health;

/// <summary>
/// Bao <see cref="HealthTracker"/>: nhận tín hiệu từ router MQTT, mỗi giây kiểm tra hạn offline,
/// ghi các lần đổi trạng thái vào CSDL, đẩy lên Station và báo cho <see cref="AlertEngine"/>.
/// </summary>
public sealed class DeviceHealthService : BackgroundService
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(60);

    private readonly HealthTracker _tracker;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<HealthTransition> _pending = new();
    private readonly TopologyCache _topology;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RealtimeNotifier _notifier;
    private readonly AlertEngine _alerts;
    private readonly UplinkPublisher _uplink;
    private readonly IMqttPublisher _publisher;
    private bool _wasConnected;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceHealthService> _logger;

    public DeviceHealthService(
        IOptions<GatewayOptions> options,
        TopologyCache topology,
        IServiceScopeFactory scopeFactory,
        RealtimeNotifier notifier,
        AlertEngine alerts,
        UplinkPublisher uplink,
        IMqttPublisher publisher,
        TimeProvider time,
        ILogger<DeviceHealthService> logger)
    {
        _tracker = new HealthTracker(TimeSpan.FromSeconds(options.Value.NodeTimeoutSeconds));
        _topology = topology;
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _alerts = alerts;
        _uplink = uplink;
        _publisher = publisher;
        _time = time;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var line in _topology.Lines)
            {
                _tracker.AddLine(line.Id);
            }

            foreach (var node in _topology.Nodes)
            {
                _tracker.AddNode(node.LineId, node.NodeByteId, now);
            }
        }

        return base.StartAsync(cancellationToken);
    }

    public void RegisterLine(int lineId)
    {
        lock (_gate)
        {
            _tracker.AddLine(lineId);
        }
    }

    public void RemoveNode(int lineId, byte nodeByteId)
    {
        lock (_gate)
        {
            _tracker.RemoveNode(lineId, nodeByteId);
        }

        _uplink.RequestSafetyRefresh();
    }

    public void RemoveLine(int lineId)
    {
        lock (_gate)
        {
            _tracker.RemoveLine(lineId);
        }

        _uplink.RequestSafetyRefresh();
    }

    public void OnNodeSeen(int lineId, byte nodeByteId, KeepAliveStatus? status = null)
    {
        IReadOnlyList<HealthTransition> transitions;
        lock (_gate)
        {
            transitions = _tracker.NodeSeen(lineId, nodeByteId, _time.GetUtcNow(), status);
        }

        Enqueue(transitions);
    }

    public void OnGatewayState(int lineId, bool online)
    {
        IReadOnlyList<HealthTransition> transitions;
        lock (_gate)
        {
            transitions = _tracker.GatewayState(lineId, online, _time.GetUtcNow());
        }

        Enqueue(transitions);
    }

    public void OnGatewayStatus(int lineId, string statusJson)
    {
        lock (_gate)
        {
            _tracker.GatewayStatus(lineId, statusJson, _time.GetUtcNow());
        }
    }

    public IReadOnlyList<GatewayHealthSnapshot> Gateways()
    {
        lock (_gate)
        {
            return _tracker.Gateways();
        }
    }

    public IReadOnlyList<NodeHealthSnapshot> Nodes()
    {
        lock (_gate)
        {
            return _tracker.Nodes();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        var nextSnapshot = _time.GetUtcNow() + SnapshotInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                var now = _time.GetUtcNow();
                var connected = _publisher.IsConnected;
                lock (_gate)
                {
                    // PC mất kết nối broker thì không bản tin nào tới được: không tính hạn offline cho node,
                    // và khi kết nối lại thì cho mọi node một hạn mới.
                    if (connected && !_wasConnected)
                    {
                        _tracker.ResetDeadlines(now);
                    }

                    if (connected)
                    {
                        Enqueue(_tracker.Tick(now));
                    }
                }

                _wasConnected = connected;

                await FlushTransitionsAsync(stoppingToken);

                if (now >= nextSnapshot)
                {
                    await PersistSnapshotAsync(stoppingToken);
                    nextSnapshot = now + SnapshotInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Health] Tick failed");
            }
        }
    }

    private void Enqueue(IReadOnlyList<HealthTransition> transitions)
    {
        foreach (var t in transitions)
        {
            _pending.Enqueue(t);
        }
    }

    private async Task FlushTransitionsAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var batch = new List<HealthTransition>();
        while (_pending.TryDequeue(out var t))
        {
            batch.Add(t);
        }

        var statuses = Nodes().ToDictionary(n => (n.LineId, n.NodeByteId));

        // Báo cảnh báo và Station trước, ghi CSDL sau: CSDL trục trặc không được làm mất cảnh báo mất kết nối.
        foreach (var t in batch)
        {
            _alerts.Post(new LivenessChanged(t));
            if (t.IsGateway)
            {
                _logger.LogInformation("[Health] Gateway L{Line}: {From} -> {To}", t.LineId, t.From, t.To);
                await _notifier.BroadcastAsync(RealtimeNotifier.GatewayChanged, new { lineId = t.LineId, state = t.To, at = t.At });
            }
            else
            {
                _logger.LogInformation("[Health] Node {Node}: {From} -> {To}", DeviceIds.Node(t.LineId, t.NodeByteId!.Value), t.From, t.To);
                await _notifier.BroadcastAsync(RealtimeNotifier.NodeChanged, new
                {
                    lineId = t.LineId,
                    nodeByteId = t.NodeByteId,
                    nodeId = DeviceIds.Node(t.LineId, t.NodeByteId.Value),
                    state = t.To,
                    at = t.At
                });
            }
        }

        _uplink.RequestSafetyRefresh();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var t in batch)
        {
            try
            {
                if (t.IsGateway)
                {
                    await PersistGatewayAsync(db, t, ct);
                }
                else
                {
                    statuses.TryGetValue((t.LineId, t.NodeByteId!.Value), out var snapshot);
                    await PersistNodeAsync(db, t, snapshot, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Trạng thái trong bộ nhớ vẫn đúng; bản chụp định kỳ (PersistSnapshotAsync) sẽ ghi lại sau.
                _logger.LogWarning("[Health] Failed to persist {Subject} transition: {Message}",
                    t.IsGateway ? $"L{t.LineId}" : DeviceIds.Node(t.LineId, t.NodeByteId!.Value), ex.Message);
            }
        }
    }

    private static Task PersistGatewayAsync(AppDbContext db, HealthTransition t, CancellationToken ct) =>
        db.Lines.Where(l => l.Id == t.LineId).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.Status, t.To)
            .SetProperty(l => l.LastSeenAt, l => t.To == Liveness.Online ? t.At : l.LastSeenAt), ct);

    private static Task PersistNodeAsync(AppDbContext db, HealthTransition t, NodeHealthSnapshot? snapshot, CancellationToken ct)
    {
        var nodeId = DeviceIds.Node(t.LineId, t.NodeByteId!.Value);
        var status = t.To switch
        {
            Liveness.Online => NodeStatus.Online,
            Liveness.Offline => NodeStatus.Offline,
            _ => NodeStatus.Unknown
        };
        var battery = (double?)snapshot?.LastStatus?.KnownBatteryPercent;
        var lastSeen = snapshot?.LastSeen?.UtcDateTime;

        return db.Nodes.Where(n => n.Id == nodeId).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.Status, status)
            .SetProperty(n => n.LastOnline, n => lastSeen ?? n.LastOnline)
            .SetProperty(n => n.BatteryLevel, n => battery ?? n.BatteryLevel), ct);
    }

    private async Task PersistSnapshotAsync(CancellationToken ct)
    {
        var online = Nodes().Where(n => n.State == Liveness.Online).ToList();
        var gateways = Gateways();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var n in online)
        {
            var nodeId = DeviceIds.Node(n.LineId, n.NodeByteId);
            var battery = (double?)n.LastStatus?.KnownBatteryPercent;
            var lastSeen = n.LastSeen?.UtcDateTime;
            await db.Nodes.Where(x => x.Id == nodeId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastOnline, x => lastSeen ?? x.LastOnline)
                .SetProperty(x => x.BatteryLevel, x => battery ?? x.BatteryLevel), ct);
        }

        foreach (var g in gateways.Where(g => g.StatusJson is not null))
        {
            var info = ParseGatewayStatus(g.StatusJson!);
            await db.Lines.Where(l => l.Id == g.LineId).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.LastSeenAt, l => g.LastSeen ?? l.LastSeenAt)
                .SetProperty(l => l.GatewayVersion, l => info.Version ?? l.GatewayVersion)
                .SetProperty(l => l.GatewayIp, l => info.Ip ?? l.GatewayIp)
                .SetProperty(l => l.GatewayMac, l => info.Mac ?? l.GatewayMac), ct);
        }
    }

    private static (string? Version, string? Ip, string? Mac) ParseGatewayStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return (Str(root, "version"), Str(root, "ip"), Str(root, "mac"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
