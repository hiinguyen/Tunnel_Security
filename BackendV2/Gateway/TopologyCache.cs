using System.Collections.Concurrent;
using BackendV2.Data;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Gateway;

public sealed record LineEntry(int Id, string Name, string GatewayHardwareId);

public sealed record NodeEntry(int LineId, byte NodeByteId, string NodeId, long? PhysicalId);

/// <summary>
/// Bản sao trong bộ nhớ của các tuyến đang hoạt động và các node đã tham gia, để đường nhận dữ liệu
/// không phải hỏi CSDL cho từng bản tin. Nạp lúc khởi động, cập nhật khi cấp tuyến/node hoặc gỡ tuyến.
/// </summary>
public sealed class TopologyCache
{
    private readonly ConcurrentDictionary<int, LineEntry> _lines = new();
    private readonly ConcurrentDictionary<(int LineId, byte NodeByteId), NodeEntry> _nodes = new();

    public IReadOnlyCollection<LineEntry> Lines => _lines.Values.OrderBy(l => l.Id).ToList();

    public IReadOnlyCollection<NodeEntry> Nodes => _nodes.Values.ToList();

    public async Task LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var lines = await db.Lines.AsNoTracking().Where(l => l.RetiredAt == null).ToListAsync(ct);
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.LineId != null && n.NodeByteId != null && n.PhysicalId != null)
            .Select(n => new { n.Id, n.LineId, n.NodeByteId, n.PhysicalId })
            .ToListAsync(ct);

        _lines.Clear();
        _nodes.Clear();
        foreach (var l in lines)
        {
            UpsertLine(new LineEntry(l.Id, l.Name, l.GatewayHardwareId));
        }

        foreach (var n in nodes.Where(n => _lines.ContainsKey(n.LineId!.Value)))
        {
            UpsertNode(new NodeEntry(n.LineId!.Value, n.NodeByteId!.Value, n.Id, n.PhysicalId));
        }
    }

    public bool IsActiveLine(int lineId) => _lines.ContainsKey(lineId);

    public bool TryGetLine(int lineId, out LineEntry line) => _lines.TryGetValue(lineId, out line!);

    public bool TryGetNode(int lineId, byte nodeByteId, out NodeEntry node) =>
        _nodes.TryGetValue((lineId, nodeByteId), out node!);

    public IReadOnlyList<NodeEntry> NodesOfLine(int lineId) =>
        _nodes.Values.Where(n => n.LineId == lineId).OrderBy(n => n.NodeByteId).ToList();

    public void UpsertLine(LineEntry line) => _lines[line.Id] = line;

    public void UpsertNode(NodeEntry node) => _nodes[(node.LineId, node.NodeByteId)] = node;

    public void RemoveNode(int lineId, byte nodeByteId) => _nodes.TryRemove((lineId, nodeByteId), out _);

    public void RemoveLine(int lineId)
    {
        _lines.TryRemove(lineId, out _);
        foreach (var key in _nodes.Keys.Where(k => k.LineId == lineId).ToList())
        {
            _nodes.TryRemove(key, out _);
        }
    }
}
