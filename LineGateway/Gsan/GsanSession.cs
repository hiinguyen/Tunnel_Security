using System.Net;
using WireProtocol;

namespace LineGateway.Gsan;

public enum SessionState { AwaitingRequest, AwaitingChallenge, AwaitingResponse, AwaitingAssignment, AwaitingAck, Bound, Closed }

/// <summary>
/// Một node trên một kết nối TCP. Dịch bắt tay A1/A3 của SDD sang bắt tay A1/A3 của giao thức NM - PC
/// (PC giữ khóa thiết bị và quyết định), rồi chuyển tiếp A7/A4 của node đã gắn lên PC.
/// Các phương thức được gọi tuần tự: OnNodeFrameAsync từ vòng đọc của kết nối, OnDownlinkAsync từ hàng đợi MQTT;
/// hai luồng đó có thể chồng nhau nên trạng thái được bảo vệ bằng khóa.
/// </summary>
public sealed class GsanSession
{
    private readonly INodeLink _link;
    private readonly ILineUplink _uplink;
    private readonly NodeSessionTable _table;
    private readonly TimeProvider _time;
    private readonly ILogger<GsanSession> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GsanSession(INodeLink link, ILineUplink uplink, NodeSessionTable table, TimeProvider time, ILogger<GsanSession> logger)
    {
        _link = link;
        _uplink = uplink;
        _table = table;
        _time = time;
        _logger = logger;
        Address = NodeSessionTable.Normalize(link.RemoteAddress);
    }

    public SessionState State { get; private set; } = SessionState.AwaitingRequest;

    public uint PhysicalId { get; private set; }

    public byte NodeId { get; private set; }

    public IPAddress Address { get; }

    public async Task OnNodeFrameAsync(WireFrame frame, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await HandleNodeFrameAsync(frame, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OnDownlinkAsync(WireFrame frame, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await HandleDownlinkAsync(frame, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Close()
    {
        if (State == SessionState.Closed) return;
        State = SessionState.Closed;
        _table.Remove(this);
        _link.Close();
    }

    private async Task HandleNodeFrameAsync(WireFrame frame, CancellationToken ct)
    {
        var command = (NodeCommand)frame.Command;
        switch (State)
        {
            case SessionState.AwaitingRequest when command == NodeCommand.JoinRequest && frame.NodeId == 0
                                                   && GsanJoin.TryDecodeRequest(frame.Payload, out var physicalId):
                PhysicalId = physicalId;
                _table.RegisterPending(this);
                State = SessionState.AwaitingChallenge;
                await _uplink.PublishAsync(0, UplinkKind.Join, NodeCommand.JoinRequest, JoinProtocol.EncodeRequest(physicalId), ct);
                return;

            case SessionState.AwaitingResponse when command == NodeCommand.JoinRequest
                                                    && GsanJoin.TryDecodeResponse(frame.Payload, out var mac):
                State = SessionState.AwaitingAssignment;
                await _uplink.PublishAsync(0, UplinkKind.Join, NodeCommand.JoinRequest, JoinProtocol.EncodeResponse(PhysicalId, mac), ct);
                return;

            case SessionState.AwaitingAck when command == NodeCommand.WriteConfig && frame.NodeId == NodeId:
                var ok = GsanJoin.IsAssignmentOk(frame.Payload);
                await _uplink.PublishAsync(0, UplinkKind.Config, NodeCommand.WriteConfig,
                    NodeAssignment.EncodeConfirm(PhysicalId, ok ? CommandResultCode.Ok : CommandResultCode.Rejected), ct);
                if (!ok)
                {
                    _logger.LogWarning("[Gsan] {Phys:X8} refused node id {Node}", PhysicalId, NodeId);
                    Close();
                    return;
                }

                State = SessionState.Bound;
                _table.Bind(this);
                _logger.LogInformation("[Gsan] {Phys:X8} at {Address} bound as node {Node}", PhysicalId, Address, NodeId);
                return;

            case SessionState.Bound when frame.NodeId != NodeId:
                _logger.LogWarning("[Gsan] node {Node}: frame claims node {Claimed}, dropped", NodeId, frame.NodeId);
                return;

            case SessionState.Bound when command == NodeCommand.SensorData:
                await _uplink.PublishAsync(NodeId, UplinkKind.Data, NodeCommand.SensorData, WithTimestamp(frame.Payload), ct);
                return;

            case SessionState.Bound when command == NodeCommand.KeepAlive && GsanHeartbeat.TryParse(frame.Payload, out var heartbeat):
                await _uplink.PublishAsync(NodeId, UplinkKind.Status, NodeCommand.KeepAlive, heartbeat.ToKeepAlive().ToBytes(), ct);
                return;

            default:
                _logger.LogDebug("[Gsan] {State}: ignoring 0x{Command:X2} from {Address}", State, frame.Command, Address);
                return;
        }
    }

    private async Task HandleDownlinkAsync(WireFrame frame, CancellationToken ct)
    {
        var command = (NodeCommand)frame.Command;
        switch (State)
        {
            // PC có thể gửi lại thách đố nếu node trả lời chậm; chấp nhận cả khi đang chờ trả lời.
            case SessionState.AwaitingChallenge or SessionState.AwaitingResponse when command == NodeCommand.JoinRequest
                && JoinProtocol.TryDecodeChallenge(frame.Payload, out _, out var nonce):
                State = SessionState.AwaitingResponse;
                await _link.SendAsync(NodeCommand.JoinRequest, 0, GsanJoin.EncodeChallenge(nonce), ct);
                return;

            case SessionState.AwaitingAssignment when command == NodeCommand.WriteConfig
                && NodeAssignment.TryDecode(frame.Payload, out _, out var nodeId, out _) && nodeId is >= 1 and <= 254:
                NodeId = nodeId;
                State = SessionState.AwaitingAck;
                await _link.SendAsync(NodeCommand.WriteConfig, nodeId, GsanJoin.EncodeAssignment(nodeId), ct);
                return;

            default:
                _logger.LogInformation("[Gsan] node {Node}: command 0x{Command:X2} from PC not supported in this release",
                    NodeId, frame.Command);
                return;
        }
    }

    /// <summary>Gắn thời điểm nhận (giờ NM, đồng bộ NTP với PC) nếu node chưa gắn, để dữ liệu đệm không bị gán sai giờ.</summary>
    private byte[] WithTimestamp(byte[] payload)
    {
        if (MetaTlv.TryGetTimestamp(SensorTlvCodec.Parse(payload), out _))
        {
            return payload;
        }

        return [.. SensorTlvCodec.Encode([MetaTlv.Timestamp(_time.GetUtcNow())]), .. payload];
    }
}
