using System.Buffers.Binary;
using WireProtocol;

namespace LineGateway.Gsan;

/// <summary>
/// Đưa lệnh từ PC (ts/{line}/n/{node}/down) tới phiên đúng: Node ID 0 tra theo ID vật lý ở 4 byte đầu payload
/// (thách đố A1 và cấp ID A3 đều bắt đầu bằng ID vật lý), 1..254 theo Node ID, 255 cho mọi node đã gắn.
/// </summary>
public sealed class DownlinkRouter(NodeSessionTable table, ILogger<DownlinkRouter> logger)
{
    public async Task RouteAsync(byte nodeId, WireFrame frame, CancellationToken ct)
    {
        if (nodeId == 0)
        {
            if (frame.Payload.Length >= 4
                && table.TryGetPending(BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload), out var pending))
            {
                await pending.OnDownlinkAsync(frame, ct);
            }
            else
            {
                logger.LogDebug("[Downlink] no joining device for 0x{Command:X2}", frame.Command);
            }

            return;
        }

        if (nodeId == 255)
        {
            foreach (var session in table.Bound)
            {
                await session.OnDownlinkAsync(frame, ct);
            }

            return;
        }

        if (table.TryGetByNode(nodeId, out var bound))
        {
            await bound.OnDownlinkAsync(frame, ct);
        }
    }
}
