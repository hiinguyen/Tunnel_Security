using BackendV2.Models;
using WireProtocol;

namespace BackendV2.Gateway.Health;

/// <summary>Đổi trạng thái của NM (<see cref="NodeByteId"/> null) hoặc node.</summary>
public sealed record HealthTransition(int LineId, byte? NodeByteId, Liveness From, Liveness To, DateTimeOffset At)
{
    public bool IsGateway => NodeByteId is null;
}

public sealed record NodeHealthSnapshot(
    int LineId, byte NodeByteId, Liveness State, DateTimeOffset? LastSeen, KeepAliveStatus? LastStatus);

public sealed record GatewayHealthSnapshot(int LineId, Liveness State, DateTimeOffset? LastSeen, string? StatusJson);

/// <summary>
/// Máy trạng thái sống/chết theo docs mục 4.7, không I/O và không tự khóa (người gọi khóa).
/// Hạn offline của node nằm trong một hàng đợi ưu tiên, mỗi node tối đa một mục, nên mỗi lần
/// <see cref="Tick"/> chỉ xử lý node đến hạn chứ không quét toàn bộ (chi phí không tăng theo số node).
/// </summary>
public sealed class HealthTracker
{
    private readonly TimeSpan _nodeTimeout;
    private readonly Dictionary<int, GatewayHealth> _gateways = new();
    private readonly Dictionary<(int LineId, byte NodeByteId), NodeHealth> _nodes = new();
    private readonly PriorityQueue<(int LineId, byte NodeByteId), DateTimeOffset> _deadlines = new();

    public HealthTracker(TimeSpan nodeTimeout)
    {
        _nodeTimeout = nodeTimeout;
    }

    public void AddLine(int lineId) => _gateways.TryAdd(lineId, new GatewayHealth());

    public void AddNode(int lineId, byte nodeByteId, DateTimeOffset now)
    {
        AddLine(lineId);
        if (_nodes.TryAdd((lineId, nodeByteId), new NodeHealth()))
        {
            Schedule((lineId, nodeByteId), _nodes[(lineId, nodeByteId)], now + _nodeTimeout);
        }
    }

    public void RemoveNode(int lineId, byte nodeByteId) => _nodes.Remove((lineId, nodeByteId));

    /// <summary>
    /// Đặt lại hạn offline cho mọi node chưa Offline. Gọi khi chính PC vừa kết nối lại broker:
    /// trong lúc PC mất kết nối không có bản tin nào tới, đó không phải lỗi của node.
    /// </summary>
    public void ResetDeadlines(DateTimeOffset now)
    {
        foreach (var (key, node) in _nodes)
        {
            var gatewayOffline = _gateways.TryGetValue(key.LineId, out var gw) && gw.State == Liveness.Offline;
            if (node.State != Liveness.Offline && !gatewayOffline)
            {
                Schedule(key, node, now + _nodeTimeout);
            }
        }
    }

    public void RemoveLine(int lineId)
    {
        _gateways.Remove(lineId);
        foreach (var key in _nodes.Keys.Where(k => k.LineId == lineId).ToList())
        {
            _nodes.Remove(key);
        }
    }

    public IReadOnlyList<HealthTransition> NodeSeen(int lineId, byte nodeByteId, DateTimeOffset now, KeepAliveStatus? status)
    {
        AddNode(lineId, nodeByteId, now);
        var node = _nodes[(lineId, nodeByteId)];
        node.LastSeen = now;
        node.LastStatus = status ?? node.LastStatus;
        Schedule((lineId, nodeByteId), node, now + _nodeTimeout);

        return node.State == Liveness.Online
            ? []
            : [Move(lineId, nodeByteId, node, Liveness.Online, now)];
    }

    public IReadOnlyList<HealthTransition> GatewayState(int lineId, bool online, DateTimeOffset now)
    {
        AddLine(lineId);
        var gateway = _gateways[lineId];
        var target = online ? Liveness.Online : Liveness.Offline;
        if (online)
        {
            gateway.LastSeen = now;
        }

        if (gateway.State == target)
        {
            return [];
        }

        var transitions = new List<HealthTransition> { new(lineId, null, gateway.State, target, now) };
        gateway.State = target;

        foreach (var (key, node) in _nodes.Where(n => n.Key.LineId == lineId))
        {
            if (!online && node.State != Liveness.Unknown)
            {
                // NM mất kết nối: không biết node còn sống hay không, và không được tính hạn offline.
                transitions.Add(Move(key.LineId, key.NodeByteId, node, Liveness.Unknown, now));
                node.Deadline = null;
            }
            else if (online && node.State == Liveness.Unknown)
            {
                Schedule(key, node, now + _nodeTimeout);
            }
        }

        return transitions;
    }

    public void GatewayStatus(int lineId, string statusJson, DateTimeOffset now)
    {
        AddLine(lineId);
        _gateways[lineId].StatusJson = statusJson;
        _gateways[lineId].LastSeen = now;
    }

    public IReadOnlyList<HealthTransition> Tick(DateTimeOffset now)
    {
        var transitions = new List<HealthTransition>();
        while (_deadlines.TryPeek(out var key, out var due) && due <= now)
        {
            _deadlines.Dequeue();
            if (!_nodes.TryGetValue(key, out var node))
            {
                continue;
            }

            node.QueuedAt = null;
            if (node.Deadline is not { } deadline)
            {
                continue;
            }

            if (deadline > now)
            {
                Schedule(key, node, deadline);
                continue;
            }

            node.Deadline = null;
            var gatewayOffline = _gateways.TryGetValue(key.LineId, out var gw) && gw.State == Liveness.Offline;
            if (!gatewayOffline && node.State != Liveness.Offline)
            {
                transitions.Add(Move(key.LineId, key.NodeByteId, node, Liveness.Offline, now));
            }
        }

        return transitions;
    }

    public IReadOnlyList<GatewayHealthSnapshot> Gateways() =>
        _gateways.Select(g => new GatewayHealthSnapshot(g.Key, g.Value.State, g.Value.LastSeen, g.Value.StatusJson))
            .OrderBy(g => g.LineId)
            .ToList();

    public IReadOnlyList<NodeHealthSnapshot> Nodes() =>
        _nodes.Select(n => new NodeHealthSnapshot(n.Key.LineId, n.Key.NodeByteId, n.Value.State, n.Value.LastSeen, n.Value.LastStatus))
            .OrderBy(n => n.LineId).ThenBy(n => n.NodeByteId)
            .ToList();

    private void Schedule((int LineId, byte NodeByteId) key, NodeHealth node, DateTimeOffset deadline)
    {
        node.Deadline = deadline;
        if (node.QueuedAt is null)
        {
            _deadlines.Enqueue(key, deadline);
            node.QueuedAt = deadline;
        }
    }

    private static HealthTransition Move(int lineId, byte nodeByteId, NodeHealth node, Liveness to, DateTimeOffset now)
    {
        var transition = new HealthTransition(lineId, nodeByteId, node.State, to, now);
        node.State = to;
        return transition;
    }

    private sealed class GatewayHealth
    {
        public Liveness State = Liveness.Unknown;
        public DateTimeOffset? LastSeen;
        public string? StatusJson;
    }

    private sealed class NodeHealth
    {
        public Liveness State = Liveness.Unknown;
        public DateTimeOffset? LastSeen;
        public KeepAliveStatus? LastStatus;

        /// <summary>Hạn offline hiện tại; null khi không tính hạn (đã offline hoặc NM mất kết nối).</summary>
        public DateTimeOffset? Deadline;

        /// <summary>Thời điểm của mục đang nằm trong hàng đợi, null nếu node không có mục nào.</summary>
        public DateTimeOffset? QueuedAt;
    }
}
