using System.Collections.Concurrent;
using System.Text;
using BackendV2.Gateway.Commands;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Ingestion;
using BackendV2.Gateway.Mqtt;
using BackendV2.Gateway.Provisioning;
using WireProtocol;

namespace BackendV2.Gateway;

/// <summary>
/// Phân loại mọi bản tin MQTT theo topic (docs mục 4.1) và mã lệnh trong khung, rồi chuyển cho service phù hợp.
/// Chặn ở đây: tuyến chưa được cấp, khung lỗi CRC, Node ID trong khung khác Node ID trên topic,
/// và dữ liệu từ node chưa tham gia.
/// </summary>
public sealed class GatewayMessageRouter : IMqttMessageHandler
{
    private const int MaxGatewayStatusBytes = 4096;
    private const int MaxWarningKeys = 10_000;
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private readonly TopologyCache _topology;
    private readonly SensorIngestion _ingestion;
    private readonly DeviceHealthService _health;
    private readonly CommandDispatcher _commands;
    private readonly NodeJoinService _join;
    private readonly GatewayProvisioningService _provisioning;
    private readonly TimeProvider _time;
    private readonly ILogger<GatewayMessageRouter> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWarning = new();

    public GatewayMessageRouter(
        TopologyCache topology,
        SensorIngestion ingestion,
        DeviceHealthService health,
        CommandDispatcher commands,
        NodeJoinService join,
        GatewayProvisioningService provisioning,
        TimeProvider time,
        ILogger<GatewayMessageRouter> logger)
    {
        _topology = topology;
        _ingestion = ingestion;
        _health = health;
        _commands = commands;
        _join = join;
        _provisioning = provisioning;
        _time = time;
        _logger = logger;
    }

    public async Task HandleAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!GatewayTopics.TryParse(topic, out var address))
        {
            WarnThrottled($"topic:{topic}", "[Router] Ignoring unexpected topic {Topic}", topic);
            return;
        }

        switch (address.Kind)
        {
            case TopicKind.ProvisionRequest:
                _provisioning.Enqueue(address.HardwareId!, payload);
                return;

            case TopicKind.GatewayState when IsActive(address.LineId):
                _health.OnGatewayState(address.LineId, Encoding.UTF8.GetString(payload.Span).Trim() == "online");
                return;

            case TopicKind.GatewayStatus when IsActive(address.LineId) && payload.Length <= MaxGatewayStatusBytes:
                _health.OnGatewayStatus(address.LineId, Encoding.UTF8.GetString(payload.Span));
                return;

            case TopicKind.NodeUplink when IsActive(address.LineId):
                await HandleNodeUplinkAsync(address, payload.ToArray(), ct);
                return;
        }
    }

    private async Task HandleNodeUplinkAsync(TopicAddress address, byte[] data, CancellationToken ct)
    {
        var line = address.LineId;
        if (!WireFrameCodec.TryDecode(data, out var frame, out var error))
        {
            WarnThrottled($"frame:{line}:{address.NodeId}", "[Router] L{Line}-N{Node}: invalid frame: {Error}", line, address.NodeId, error);
            return;
        }

        var command = (NodeCommand)frame!.Command;
        var uplink = address.Uplink!.Value;

        if (frame.NodeId != address.NodeId)
        {
            WarnThrottled($"spoof:{line}:{address.NodeId}",
                "[Router] L{Line}: frame node id {FrameNode} does not match topic node {TopicNode}, dropped",
                line, frame.NodeId, address.NodeId);
            return;
        }

        // Node chưa có ID (0): chỉ được tham gia (A1) và xác nhận cấp ID (A3).
        if (address.NodeId == GatewayTopics.UnassignedNodeId)
        {
            if (uplink == UplinkKind.Join && command == NodeCommand.JoinRequest)
            {
                await _join.OnJoinAsync(line, frame.Payload, ct);
            }
            else if (uplink == UplinkKind.Config && command == NodeCommand.WriteConfig)
            {
                await _join.OnAssignmentConfirmAsync(line, frame.Payload);
            }

            return;
        }

        if (!_topology.TryGetNode(line, address.NodeId, out var node))
        {
            WarnThrottled($"unknown-node:{line}:{address.NodeId}",
                "[Router] L{Line}-N{Node}: uplink from a node that has not joined, dropped", line, address.NodeId);
            return;
        }

        switch (uplink, command)
        {
            case (UplinkKind.Data, NodeCommand.SensorData):
                _health.OnNodeSeen(line, node.NodeByteId);
                await _ingestion.HandleAsync(node, frame.Payload);
                break;

            case (UplinkKind.Status, NodeCommand.KeepAlive):
                KeepAliveStatus.TryParse(frame.Payload, out var status);
                _health.OnNodeSeen(line, node.NodeByteId, status);
                break;

            case (UplinkKind.Config, NodeCommand.ReadConfig or NodeCommand.WriteConfig):
            case (UplinkKind.Ack, NodeCommand.Reset or NodeCommand.Control):
                _health.OnNodeSeen(line, node.NodeByteId);
                if (!_commands.OnResponse(line, node.NodeByteId, command, frame.Payload))
                {
                    _logger.LogDebug("[Router] {Node}: unsolicited {Command} response", node.NodeId, command);
                }

                break;

            default:
                WarnThrottled($"mismatch:{line}:{address.NodeId}:{command}",
                    "[Router] {Node}: command {Command} not allowed on uplink {Uplink}", node.NodeId, command, uplink);
                break;
        }
    }

    private bool IsActive(int lineId)
    {
        if (_topology.IsActiveLine(lineId))
        {
            return true;
        }

        WarnThrottled($"line:{lineId}", "[Router] Ignoring traffic from line {Line} which is not provisioned", lineId);
        return false;
    }

    private void WarnThrottled(string key, string message, params object?[] args)
    {
        var now = _time.GetUtcNow();
        if (_lastWarning.TryGetValue(key, out var last) && now - last < WarningInterval)
        {
            return;
        }

        // Khóa có thể chứa topic do client gửi tới: giới hạn kích thước để không phình vô hạn.
        if (_lastWarning.Count >= MaxWarningKeys)
        {
            _lastWarning.Clear();
        }

        _lastWarning[key] = now;
#pragma warning disable CA2254 // Template được truyền từ chỗ gọi, luôn là hằng số.
        _logger.LogWarning(message, args);
#pragma warning restore CA2254
    }
}
