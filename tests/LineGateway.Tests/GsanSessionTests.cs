using LineGateway.Gsan;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WireProtocol;

namespace LineGateway.Tests;

public class GsanSessionTests
{
    private const uint Phys = 0x12345678;
    private readonly FakeUplink _uplink = new();
    private readonly NodeSessionTable _table = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));

    private (GsanSession Session, FakeLink Link) NewSession(string ip = "10.1.1.7")
    {
        var link = new FakeLink(ip);
        return (new GsanSession(link, _uplink, _table, _time, NullLogger<GsanSession>.Instance), link);
    }

    private DownlinkRouter Router() => new(_table, NullLogger<DownlinkRouter>.Instance);

    private static WireFrame Node(NodeCommand cmd, byte node, byte[] payload) => new((byte)cmd, node, payload);

    private async Task<(GsanSession, FakeLink)> JoinAsync(byte nodeId = 7)
    {
        var (s, link) = NewSession();
        await s.OnNodeFrameAsync(Node(NodeCommand.JoinRequest, 0, GsanJoin.EncodeRequest(Phys)), default);
        await Router().RouteAsync(0, Node(NodeCommand.JoinRequest, 0, JoinProtocol.EncodeChallenge(Phys, 0xCAFEBABE)), default);
        await s.OnNodeFrameAsync(Node(NodeCommand.JoinRequest, 0, GsanJoin.EncodeResponse(0x160B6BBA)), default);
        await Router().RouteAsync(0, Node(NodeCommand.WriteConfig, 0, NodeAssignment.Encode(Phys, nodeId)), default);
        await s.OnNodeFrameAsync(Node(NodeCommand.WriteConfig, nodeId, GsanJoin.AssignmentOk), default);
        return (s, link);
    }

    [Fact]
    public async Task Join_is_translated_between_sdd_and_lan_protocols()
    {
        var (s, link) = await JoinAsync();

        Assert.Equal(SessionState.Bound, s.State);
        Assert.Equal(7, s.NodeId);
        Assert.Equal(
        [
            (NodeCommand.JoinRequest, (byte)0, GsanJoin.EncodeChallenge(0xCAFEBABE)),
            (NodeCommand.WriteConfig, (byte)7, GsanJoin.EncodeAssignment(7))
        ], link.Sent.Select(x => (x.Command, x.Node, x.Payload)), new SentComparer());
        Assert.Equal(3, _uplink.Published.Count);
        Assert.Equal((0, UplinkKind.Join), (_uplink.Published[0].Node, _uplink.Published[0].Kind));
        Assert.Equal(JoinProtocol.EncodeRequest(Phys), _uplink.Published[0].Payload);
        Assert.Equal(JoinProtocol.EncodeResponse(Phys, 0x160B6BBA), _uplink.Published[1].Payload);
        Assert.Equal((0, UplinkKind.Config, NodeCommand.WriteConfig),
            (_uplink.Published[2].Node, _uplink.Published[2].Kind, _uplink.Published[2].Command));
        Assert.True(NodeAssignment.TryDecodeConfirm(_uplink.Published[2].Payload, out var phys, out var result));
        Assert.Equal((Phys, CommandResultCode.Ok), (phys, result));
        Assert.True(_table.TryGetByNode(7, out _));
        Assert.True(_table.TryGetByAddress(System.Net.IPAddress.Parse("10.1.1.7"), out _));
    }

    [Fact]
    public async Task Sensor_data_is_forwarded_with_receive_timestamp()
    {
        var (s, _) = await JoinAsync();
        var radar = SensorTlvCodec.Encode([new SensorTlvEntry(0x04, 1, [0])]);

        await s.OnNodeFrameAsync(Node(NodeCommand.SensorData, 7, radar), default);

        var up = _uplink.Published[^1];
        Assert.Equal((7, UplinkKind.Data, NodeCommand.SensorData), (up.Node, up.Kind, up.Command));
        var entries = SensorTlvCodec.Parse(up.Payload);
        Assert.True(MetaTlv.TryGetTimestamp(entries, out var at));
        Assert.Equal(_time.GetUtcNow(), at);
        Assert.Contains(entries, e => e.TypeByte == 0x04);
    }

    [Fact]
    public async Task Heartbeat_is_converted_to_lan_keepalive()
    {
        var (s, _) = await JoinAsync();

        await s.OnNodeFrameAsync(Node(NodeCommand.KeepAlive, 7,
            new GsanHeartbeat(2, 0, GsanSensorFlags.Radar | GsanSensorFlags.Camera, 50f).ToBytes()), default);

        var up = _uplink.Published[^1];
        Assert.Equal((7, UplinkKind.Status, NodeCommand.KeepAlive), (up.Node, up.Kind, up.Command));
        Assert.True(KeepAliveStatus.TryParse(up.Payload, out var status));
        Assert.Equal(NodePowerState.On, status!.State);
    }

    [Fact]
    public async Task Frames_with_another_node_id_and_data_before_join_are_dropped()
    {
        var (fresh, _) = NewSession("10.1.1.9");
        await fresh.OnNodeFrameAsync(Node(NodeCommand.SensorData, 7, [4, 1, 1, 0, 0]), default);
        var (s, _) = await JoinAsync();
        var before = _uplink.Published.Count;

        await s.OnNodeFrameAsync(Node(NodeCommand.SensorData, 8, [4, 1, 1, 0, 0]), default);

        Assert.Equal(before, _uplink.Published.Count);
        Assert.Equal(SessionState.AwaitingRequest, fresh.State);
    }

    [Fact]
    public async Task Reconnecting_device_replaces_its_old_session()
    {
        var (old, oldLink) = await JoinAsync();
        var (fresh, _) = NewSession();

        await fresh.OnNodeFrameAsync(Node(NodeCommand.JoinRequest, 0, GsanJoin.EncodeRequest(Phys)), default);

        Assert.True(oldLink.Closed);
        Assert.Equal(SessionState.Closed, old.State);
        Assert.False(_table.TryGetByNode(7, out _));
        Assert.True(_table.TryGetPending(Phys, out var pending));
        Assert.Same(fresh, pending);
    }

    [Fact]
    public async Task Refused_assignment_is_reported_and_closes_the_session()
    {
        var (s, link) = NewSession();
        await s.OnNodeFrameAsync(Node(NodeCommand.JoinRequest, 0, GsanJoin.EncodeRequest(Phys)), default);
        await Router().RouteAsync(0, Node(NodeCommand.JoinRequest, 0, JoinProtocol.EncodeChallenge(Phys, 1)), default);
        await s.OnNodeFrameAsync(Node(NodeCommand.JoinRequest, 0, GsanJoin.EncodeResponse(2)), default);
        await Router().RouteAsync(0, Node(NodeCommand.WriteConfig, 0, NodeAssignment.Encode(Phys, 7)), default);

        await s.OnNodeFrameAsync(Node(NodeCommand.WriteConfig, 7, "NO"u8.ToArray()), default);

        Assert.True(NodeAssignment.TryDecodeConfirm(_uplink.Published[^1].Payload, out _, out var result));
        Assert.Equal(CommandResultCode.Rejected, result);
        Assert.True(link.Closed);
    }

    private sealed class SentComparer : IEqualityComparer<(NodeCommand, byte, byte[])>
    {
        public bool Equals((NodeCommand, byte, byte[]) a, (NodeCommand, byte, byte[]) b) =>
            a.Item1 == b.Item1 && a.Item2 == b.Item2 && a.Item3.SequenceEqual(b.Item3);

        public int GetHashCode((NodeCommand, byte, byte[]) x) => HashCode.Combine(x.Item1, x.Item2);
    }
}
