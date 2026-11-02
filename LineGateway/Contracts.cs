using System.Net;
using WireProtocol;

namespace LineGateway;

/// <summary>Kênh lên PC: đóng khung LAN và publish lên ts/{line}/n/{node}/up/{kind}.</summary>
public interface ILineUplink
{
    Task PublishAsync(byte nodeId, UplinkKind kind, NodeCommand command, byte[] payload, CancellationToken ct);
}

/// <summary>Kết nối TCP tới một node, gửi khung theo định dạng GSAN.</summary>
public interface INodeLink
{
    IPAddress RemoteAddress { get; }

    Task SendAsync(NodeCommand command, byte nodeId, byte[] payload, CancellationToken ct);

    void Close();
}

/// <summary>Đích của video đã ghép theo node.</summary>
public interface ICameraSink
{
    void PostFrame(byte nodeId, byte[] jpeg);

    void PostDetections(byte nodeId, string json);
}

public interface ILineIdentity
{
    int? LineId { get; }
}
