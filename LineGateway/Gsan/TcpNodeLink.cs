using System.Net;
using System.Net.Sockets;
using WireProtocol;

namespace LineGateway.Gsan;

/// <summary>Gửi khung GSAN xuống node; mỗi lần một khung vì có thể gửi đồng thời từ vòng đọc và từ lệnh của PC.</summary>
public sealed class TcpNodeLink(TcpClient client) : INodeLink
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public IPAddress RemoteAddress { get; } = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;

    public async Task SendAsync(NodeCommand command, byte nodeId, byte[] payload, CancellationToken ct)
    {
        var frame = WireFrameCodec.Encode((byte)command, nodeId, payload, FrameFormat.Gsan);
        await _sendGate.WaitAsync(ct);
        try
        {
            await client.GetStream().WriteAsync(frame, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Close() => client.Close();
}
