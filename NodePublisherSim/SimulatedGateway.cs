using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using WireProtocol;

namespace NodePublisherSim;

/// <summary>
/// Giả lập một NM (Gateway tuyến) cùng các node phía sau, theo docs/lan-architecture-protocol.md:
/// xin cấp tuyến, kết nối lại bằng tài khoản của tuyến (có Last Will), cho từng node tham gia bằng A1/A3,
/// rồi gửi A7 và A4 theo chu kỳ và trả lời lệnh A2/A3/A5/A6.
/// Trong thực tế node nói chuyện với NM qua TCP; ở đây NM và node chạy chung một tiến trình nên dùng chung một kết nối MQTT.
/// </summary>
public sealed class SimulatedGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SimOptions _options;
    private readonly int _index;
    private readonly string _hardwareId;
    private readonly List<SimulatedNode> _nodes;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private IMqttClient? _client;
    private int _lineId;
    private long _published;

    public SimulatedGateway(SimOptions options, int index)
    {
        _options = options;
        _index = index;
        _hardwareId = $"{options.GatewayIdPrefix}-{index}";
        var secret = Encoding.UTF8.GetBytes(options.DeviceSecret);
        _nodes = Enumerable.Range(1, options.NodesPerLine)
            .Select(n =>
            {
                var physicalId = SimOptions.PhysicalId(index, n);
                return new SimulatedNode(n, physicalId, JoinProtocol.DeriveDeviceKey(secret, physicalId), seed: (int)physicalId);
            })
            .ToList();
    }

    public long Published => Interlocked.Read(ref _published);

    public int JoinedNodes => _nodes.Count(n => n.NodeId is not null);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (lineId, username, password) = await ProvisionAsync(ct);
                _lineId = lineId;
                await RunLineAsync(username, password, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"error: {ex.Message}; restarting in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        if (_client is { IsConnected: true })
        {
            // Ngắt kết nối sạch không kích hoạt Last Will, nên tự báo offline trước khi thoát.
            await PublishAsync(GatewayTopics.GatewayState(_lineId), Encoding.UTF8.GetBytes("offline"), retain: true, CancellationToken.None);
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }
    }

    private async Task<(int LineId, string? Username, string? Password)> ProvisionAsync(CancellationToken ct)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            using var doc = JsonDocument.Parse(e.ApplicationMessage.Payload.ToArray());
            response.TrySetResult(doc.RootElement.Clone());
            return Task.CompletedTask;
        };

        var builder = new MqttClientOptionsBuilder()
            .WithClientId(_hardwareId)
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            .WithCleanSession(true);
        if (_options.ProvisionPassword is not null)
        {
            builder = builder.WithCredentials(_options.ProvisionUsername, _options.ProvisionPassword);
        }

        await client.ConnectAsync(builder.Build(), ct);
        await client.SubscribeAsync(GatewayTopics.ProvisionResponse(_hardwareId), MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            mac = $"02:51:00:00:{_index >> 8:X2}:{_index & 0xFF:X2}",
            ip = $"10.{Math.Min(_index, 254)}.0.1",
            version = "sim-1.0"
        }, Json);

        while (true)
        {
            Log("requesting line assignment");
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(GatewayTopics.ProvisionRequest(_hardwareId))
                .WithPayload(request)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), ct);

            var completed = await Task.WhenAny(response.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
            if (completed != response.Task)
            {
                continue;
            }

            var result = response.Task.Result;
            if (result.GetProperty("status").GetString() != "approved")
            {
                Log("line assignment rejected, retrying in 60s");
                response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                continue;
            }

            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
            var lineId = result.GetProperty("lineId").GetInt32();
            var username = result.TryGetProperty("username", out var u) ? u.GetString() : null;
            var password = result.TryGetProperty("password", out var p) ? p.GetString() : null;
            Log($"assigned line {lineId}");
            return (lineId, username, password);
        }
    }

    private async Task RunLineAsync(string? username, string? password, CancellationToken ct)
    {
        _client?.Dispose();
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e => OnDownlinkAsync(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray());

        var builder = new MqttClientOptionsBuilder()
            .WithClientId($"{_hardwareId}-line")
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithCleanSession(true)
            .WithWillTopic(GatewayTopics.GatewayState(_lineId))
            .WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (username is not null)
        {
            builder = builder.WithCredentials(username, password);
        }
        else if (_options.ProvisionPassword is not null)
        {
            builder = builder.WithCredentials(_options.ProvisionUsername, _options.ProvisionPassword);
        }

        await _client.ConnectAsync(builder.Build(), ct);
        await _client.SubscribeAsync(GatewayTopics.LineDownlinkFilter(_lineId), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await PublishAsync(GatewayTopics.GatewayState(_lineId), Encoding.UTF8.GetBytes("online"), retain: true, ct);
        Log($"online as line {_lineId}");

        var nextStatus = DateTimeOffset.MinValue;
        var stagger = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _nodes.Count));
        for (var i = 0; i < _nodes.Count; i++)
        {
            _nodes[i].NextJoinAttempt = DateTimeOffset.UtcNow + stagger * i;
        }

        while (!ct.IsCancellationRequested && _client.IsConnected)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= nextStatus)
            {
                await PublishGatewayStatusAsync(ct);
                nextStatus = now + TimeSpan.FromSeconds(30);
            }

            foreach (var node in _nodes)
            {
                await TickNodeAsync(node, now, ct);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        if (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("connection lost");
        }
    }

    private async Task TickNodeAsync(SimulatedNode node, DateTimeOffset now, CancellationToken ct)
    {
        if (node.NodeId is not { } nodeId)
        {
            if (now >= node.NextJoinAttempt)
            {
                node.NextJoinAttempt = now + TimeSpan.FromSeconds(5);
                await SendAsync(GatewayTopics.Join(_lineId), NodeCommand.JoinRequest, GatewayTopics.UnassignedNodeId,
                    JoinProtocol.EncodeRequest(node.PhysicalId), ct);
            }

            return;
        }

        if (now < node.PausedUntil || node.Power == NodePowerState.Off)
        {
            return;
        }

        if (now >= node.NextKeepAlive)
        {
            node.NextKeepAlive = now + _options.KeepAliveInterval;
            await SendAsync(GatewayTopics.NodeUplink(_lineId, nodeId, UplinkKind.Status), NodeCommand.KeepAlive, nodeId,
                node.BuildKeepAlivePayload(), ct);
        }

        if (node.Power != NodePowerState.Sleep && now >= node.NextData)
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(_options.PublishInterval.TotalMilliseconds, node.SampleIntervalMs));
            node.NextData = now + interval;
            node.MaybeStartEvent(now, _options.EventProbability * interval.TotalSeconds);
            await SendAsync(GatewayTopics.NodeUplink(_lineId, nodeId, UplinkKind.Data), NodeCommand.SensorData, nodeId,
                node.BuildSensorPayload(now), ct, qos: MqttQualityOfServiceLevel.AtMostOnce);
        }
    }

    private async Task OnDownlinkAsync(string topic, byte[] data)
    {
        if (!GatewayTopics.TryParse(topic, out var address) || address.Kind != TopicKind.NodeDownlink
            || !WireFrameCodec.TryDecode(data, out var frame, out _))
        {
            return;
        }

        var command = (NodeCommand)frame!.Command;
        try
        {
            if (address.NodeId == GatewayTopics.UnassignedNodeId)
            {
                await OnUnassignedAsync(command, frame.Payload);
            }
            else if (address.NodeId == GatewayTopics.BroadcastNodeId)
            {
                foreach (var node in _nodes.Where(n => n.NodeId is not null))
                {
                    Apply(node, command, frame.Payload);
                }
            }
            else if (_nodes.FirstOrDefault(n => n.NodeId == address.NodeId) is { } node)
            {
                var (uplink, response) = Apply(node, command, frame.Payload);
                if (response is not null)
                {
                    await SendAsync(GatewayTopics.NodeUplink(_lineId, address.NodeId, uplink), command, address.NodeId, response, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"downlink {command} failed: {ex.Message}");
        }
    }

    private async Task OnUnassignedAsync(NodeCommand command, byte[] payload)
    {
        if (command == NodeCommand.JoinRequest && JoinProtocol.TryDecodeChallenge(payload, out var physicalId, out var nonce)
            && _nodes.FirstOrDefault(n => n.PhysicalId == physicalId) is { } challenged)
        {
            var mac = JoinProtocol.ComputeMac(challenged.Key, physicalId, nonce);
            await SendAsync(GatewayTopics.Join(_lineId), NodeCommand.JoinRequest, GatewayTopics.UnassignedNodeId,
                JoinProtocol.EncodeResponse(physicalId, mac), CancellationToken.None);
        }
        else if (command == NodeCommand.WriteConfig && NodeAssignment.TryDecode(payload, out var assignedId, out var nodeId, out var config)
                 && _nodes.FirstOrDefault(n => n.PhysicalId == assignedId) is { } assigned)
        {
            assigned.NodeId = nodeId;
            assigned.SampleIntervalMs = config.SampleIntervalMs ?? assigned.SampleIntervalMs;
            assigned.NextData = DateTimeOffset.UtcNow;
            assigned.NextKeepAlive = DateTimeOffset.UtcNow;
            await SendAsync(GatewayTopics.NodeUplink(_lineId, GatewayTopics.UnassignedNodeId, UplinkKind.Config), NodeCommand.WriteConfig,
                GatewayTopics.UnassignedNodeId, NodeAssignment.EncodeConfirm(assignedId, CommandResultCode.Ok), CancellationToken.None);
            Log($"device {assignedId:X8} joined as N{nodeId} (ip {config.IpAddress})");
        }
    }

    private (UplinkKind Uplink, byte[]? Response) Apply(SimulatedNode node, NodeCommand command, byte[] payload)
    {
        switch (command)
        {
            case NodeCommand.ReadConfig:
                return (UplinkKind.Config, new NodeConfig(node.NodeId, node.SampleIntervalMs, NodeType: 1,
                    IpAddress: new IPAddress([10, (byte)_lineId, 1, node.NodeId!.Value])).ToBytes());

            case NodeCommand.WriteConfig:
                var config = NodeConfig.FromEntries(SensorTlvCodec.Parse(payload));
                node.SampleIntervalMs = config.SampleIntervalMs ?? node.SampleIntervalMs;
                return (UplinkKind.Config, CommandAck.Encode(CommandResultCode.Ok));

            case NodeCommand.Reset:
                node.PausedUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
                node.Power = NodePowerState.On;
                Log($"N{node.NodeId} reset");
                return (UplinkKind.Ack, CommandAck.Encode(CommandResultCode.Ok));

            case NodeCommand.Control:
                var ok = node.ApplyControl(payload);
                Log($"N{node.NodeId} control -> power {node.Power}");
                return (UplinkKind.Ack, CommandAck.Encode(ok ? CommandResultCode.Ok : CommandResultCode.InvalidPayload));

            default:
                return (UplinkKind.Ack, null);
        }
    }

    private Task PublishGatewayStatusAsync(CancellationToken ct) => PublishAsync(
        GatewayTopics.GatewayStatus(_lineId),
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            nodes = JoinedNodes,
            bufferFillPercent = 0,
            version = "sim-1.0",
            ip = $"10.{_lineId}.0.1",
            mac = $"02:51:00:00:{_index >> 8:X2}:{_index & 0xFF:X2}"
        }, Json),
        retain: true, ct);

    private Task SendAsync(string topic, NodeCommand command, byte nodeId, byte[] payload, CancellationToken ct,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce) =>
        PublishAsync(topic, WireFrameCodec.Encode((byte)command, nodeId, payload), retain: false, ct, qos);

    private async Task PublishAsync(string topic, byte[] payload, bool retain, CancellationToken ct,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce)
    {
        await _client!.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build(), ct);
        Interlocked.Increment(ref _published);
    }

    private void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{_hardwareId}] {message}");
}
