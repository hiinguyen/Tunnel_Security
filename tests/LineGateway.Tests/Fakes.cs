using System.Net;
using LineGateway;
using LineGateway.Gsan;
using WireProtocol;

namespace LineGateway.Tests;

internal sealed class FakeUplink : ILineUplink
{
    public List<(byte Node, UplinkKind Kind, NodeCommand Command, byte[] Payload)> Published { get; } = [];

    public Task PublishAsync(byte nodeId, UplinkKind kind, NodeCommand command, byte[] payload, CancellationToken ct)
    {
        lock (Published) Published.Add((nodeId, kind, command, payload));
        return Task.CompletedTask;
    }
}

internal sealed class FakeLink(string ip) : INodeLink
{
    public IPAddress RemoteAddress { get; } = IPAddress.Parse(ip);
    public List<(NodeCommand Command, byte Node, byte[] Payload)> Sent { get; } = [];
    public bool Closed { get; private set; }

    public Task SendAsync(NodeCommand command, byte nodeId, byte[] payload, CancellationToken ct)
    {
        Sent.Add((command, nodeId, payload));
        return Task.CompletedTask;
    }

    public void Close() => Closed = true;
}

internal sealed class FakeSink : ICameraSink
{
    public List<(byte Node, byte[] Jpeg)> Frames { get; } = [];
    public List<(byte Node, string Json)> Detections { get; } = [];

    public void PostFrame(byte nodeId, byte[] jpeg) { lock (Frames) Frames.Add((nodeId, jpeg)); }

    public void PostDetections(byte nodeId, string json) { lock (Detections) Detections.Add((nodeId, json)); }
}

internal static class TestSessions
{
    /// <summary>Đưa một phiên ở địa chỉ <paramref name="ip"/> tới trạng thái Bound với Node ID cho trước.</summary>
    public static async Task<GsanSession> BindAsync(NodeSessionTable table, string ip, byte nodeId, uint phys = 0x01020304)
    {
        var uplink = new FakeUplink();
        var session = new GsanSession(new FakeLink(ip), uplink, table, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GsanSession>.Instance);
        var router = new DownlinkRouter(table, Microsoft.Extensions.Logging.Abstractions.NullLogger<DownlinkRouter>.Instance);
        await session.OnNodeFrameAsync(new WireFrame(0xA1, 0, GsanJoin.EncodeRequest(phys)), default);
        await router.RouteAsync(0, new WireFrame(0xA1, 0, JoinProtocol.EncodeChallenge(phys, 1)), default);
        await session.OnNodeFrameAsync(new WireFrame(0xA1, 0, GsanJoin.EncodeResponse(2)), default);
        await router.RouteAsync(0, new WireFrame(0xA3, 0, NodeAssignment.Encode(phys, nodeId)), default);
        await session.OnNodeFrameAsync(new WireFrame(0xA3, nodeId, GsanJoin.AssignmentOk), default);
        return session;
    }
}
