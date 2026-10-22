using System.Net;
using LineGateway;
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
