using System.Net;

namespace LineGateway.Gsan;

/// <summary>
/// Phiên node theo ID vật lý (khi đang tham gia), theo Node ID và theo địa chỉ IP (khi đã gắn).
/// Địa chỉ IP cho phép gán luồng video UDP/TCP tới đúng node, vì gói video không mang Node ID.
/// Một thiết bị kết nối lại thay thế phiên cũ (phiên TCP cũ có thể còn treo sau khi mất mạng).
/// </summary>
public sealed class NodeSessionTable
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, GsanSession> _byPhysical = new();
    private readonly Dictionary<byte, GsanSession> _byNode = new();
    private readonly Dictionary<IPAddress, GsanSession> _byAddress = new();

    public IReadOnlyList<GsanSession> Bound
    {
        get { lock (_gate) return _byNode.Values.ToList(); }
    }

    public void RegisterPending(GsanSession session)
    {
        GsanSession? replaced;
        lock (_gate)
        {
            _byPhysical.TryGetValue(session.PhysicalId, out replaced);
            if (replaced is not null) RemoveLocked(replaced);
            _byPhysical[session.PhysicalId] = session;
        }

        replaced?.Close();
    }

    public bool TryGetPending(uint physicalId, out GsanSession session)
    {
        lock (_gate)
        {
            return _byPhysical.TryGetValue(physicalId, out session!) && session.State != SessionState.Bound;
        }
    }

    public void Bind(GsanSession session)
    {
        GsanSession? replaced;
        lock (_gate)
        {
            _byNode.TryGetValue(session.NodeId, out replaced);
            if (replaced is not null && replaced != session) RemoveLocked(replaced);
            else replaced = null;
            _byNode[session.NodeId] = session;
            _byAddress[session.Address] = session;
        }

        replaced?.Close();
    }

    public bool TryGetByNode(byte nodeId, out GsanSession session)
    {
        lock (_gate) return _byNode.TryGetValue(nodeId, out session!);
    }

    public bool TryGetByAddress(IPAddress address, out GsanSession session)
    {
        lock (_gate) return _byAddress.TryGetValue(Normalize(address), out session!);
    }

    public void Remove(GsanSession session)
    {
        lock (_gate) RemoveLocked(session);
    }

    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private void RemoveLocked(GsanSession session)
    {
        if (_byPhysical.TryGetValue(session.PhysicalId, out var p) && p == session) _byPhysical.Remove(session.PhysicalId);
        if (_byNode.TryGetValue(session.NodeId, out var n) && n == session) _byNode.Remove(session.NodeId);
        if (_byAddress.TryGetValue(session.Address, out var a) && a == session) _byAddress.Remove(session.Address);
    }
}
