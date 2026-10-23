using System.Buffers;
using System.Text;
using System.Text.Json;
using LineGateway.Gsan;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using WireProtocol;

namespace LineGateway.Mqtt;

/// <summary>Broker từ chối tài khoản của tuyến (mật khẩu đổi hoặc tuyến đã gỡ): phải xin cấp tuyến lại.</summary>
internal sealed class LineCredentialsRejectedException(MqttClientConnectResultCode code)
    : Exception($"line credentials rejected by broker: {code}");

/// <summary>NM là một MQTT client của broker trên PC (lan doc 4): xin cấp tuyến, báo online/offline, chuyển khung hai chiều.</summary>
public sealed class MqttLineUplink(
    IOptions<LineGatewayOptions> options,
    DownlinkRouter router,
    NodeSessionTable sessions,
    ILogger<MqttLineUplink> logger) : BackgroundService, ILineUplink, ILineIdentity
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(30);

    private readonly LineGatewayOptions _options = options.Value;
    private readonly LineCredentialsStore _store = new(options.Value.StatePath);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private IMqttClient? _client;

    public int? LineId { get; private set; }

    private string HardwareId => string.IsNullOrWhiteSpace(_options.HardwareId)
        ? File.ReadAllText("/etc/machine-id").Trim()
        : _options.HardwareId;

    public async Task PublishAsync(byte nodeId, UplinkKind kind, NodeCommand command, byte[] payload, CancellationToken ct)
    {
        if (_client is not { IsConnected: true } client || LineId is not { } line)
        {
            logger.LogDebug("[Mqtt] not connected, dropped 0x{Command:X2} from node {Node}", (byte)command, nodeId);
            return;
        }

        // QoS theo lan doc 4.1: dữ liệu A7 QoS 0, còn lại QoS 1; trạng thái node là retained.
        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(GatewayTopics.NodeUplink(line, nodeId, kind))
            .WithPayload(WireFrameCodec.Encode((byte)command, nodeId, payload))
            .WithQualityOfServiceLevel(kind == UplinkKind.Data ? MqttQualityOfServiceLevel.AtMostOnce : MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(kind == UplinkKind.Status)
            .Build(), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var credentials = _store.Load() ?? await ProvisionAsync(ct);
                await RunLineAsync(credentials, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (LineCredentialsRejectedException)
            {
                logger.LogWarning("[Mqtt] stored line credentials rejected, requesting a line again");
                _store.Delete();
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Mqtt] {Error}; retrying in {Delay}s", ex.Message, RetryDelay.TotalSeconds);
            }

            await Task.Delay(RetryDelay, ct);
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true } client && LineId is { } line)
        {
            // Ngắt kết nối sạch không kích hoạt Last Will, nên tự báo offline.
            await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(GatewayTopics.GatewayState(line))
                .WithPayload("offline"u8.ToArray()).WithRetainFlag(true)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
        }

        await base.StopAsync(ct);
    }

    private MqttClientOptionsBuilder BaseOptions(string clientId)
    {
        var builder = new MqttClientOptionsBuilder().WithClientId(clientId).WithTcpServer(_options.MqttHost, _options.MqttPort);
        return _options.MqttUseTls ? builder.WithTlsOptions(o => o.UseTls()) : builder;
    }

    private async Task<LineCredentials> ProvisionAsync(CancellationToken ct)
    {
        var hardwareId = HardwareId;
        using var client = new MqttClientFactory().CreateMqttClient();
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            using var doc = JsonDocument.Parse(e.ApplicationMessage.Payload.ToArray());
            response.TrySetResult(doc.RootElement.Clone());
            return Task.CompletedTask;
        };

        // MQTTnet 5 không ném lỗi khi broker từ chối đăng nhập mà trả mã kết quả, nên phải kiểm tra.
        var login = await client.ConnectAsync(BaseOptions(hardwareId).WithCleanSession(true)
            .WithCredentials(_options.ProvisionUsername, _options.ProvisionPassword).Build(), ct);
        if (login.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"provisioning login refused: {login.ResultCode}");
        }

        await client.SubscribeAsync(GatewayTopics.ProvisionResponse(hardwareId), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { version = typeof(MqttLineUplink).Assembly.GetName().Version?.ToString() });

        while (true)
        {
            logger.LogInformation("[Mqtt] {Hw}: requesting a line", hardwareId);
            await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(GatewayTopics.ProvisionRequest(hardwareId))
                .WithPayload(request).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
            var completed = await Task.WhenAny(response.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
            if (completed != response.Task)
            {
                continue;   // chờ operator duyệt trên Station
            }

            var result = response.Task.Result;
            if (result.GetProperty("status").GetString() != "approved")
            {
                logger.LogWarning("[Mqtt] line request rejected, retrying in 60s");
                response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                continue;
            }

            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
            var credentials = new LineCredentials(result.GetProperty("lineId").GetInt32(),
                result.TryGetProperty("username", out var u) ? u.GetString() : null,
                result.TryGetProperty("password", out var p) ? p.GetString() : null);
            _store.Save(credentials);
            return credentials;
        }
    }

    private async Task RunLineAsync(LineCredentials credentials, CancellationToken ct)
    {
        var line = credentials.LineId;
        _client?.Dispose();
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e => OnDownlinkAsync(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray(), ct);

        var builder = BaseOptions($"{HardwareId}-line").WithKeepAlivePeriod(TimeSpan.FromSeconds(15)).WithCleanSession(true)
            .WithWillTopic(GatewayTopics.GatewayState(line)).WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillRetain(true).WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        builder = credentials.Username is not null
            ? builder.WithCredentials(credentials.Username, credentials.Password)
            : builder.WithCredentials(_options.ProvisionUsername, _options.ProvisionPassword);

        var login = await _client.ConnectAsync(builder.Build(), ct);
        if (login.ResultCode is MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized
            && credentials.Username is not null)
        {
            throw new LineCredentialsRejectedException(login.ResultCode);
        }

        if (login.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"broker refused line {line}: {login.ResultCode}");
        }

        LineId = line;
        await _client.SubscribeAsync(GatewayTopics.LineDownlinkFilter(line), MqttQualityOfServiceLevel.AtLeastOnce, ct);
        await PublishRetainedAsync(GatewayTopics.GatewayState(line), "online"u8.ToArray(), ct);
        logger.LogInformation("[Mqtt] online as line {Line}", line);

        while (!ct.IsCancellationRequested && _client.IsConnected)
        {
            await PublishRetainedAsync(GatewayTopics.GatewayStatus(line), JsonSerializer.SerializeToUtf8Bytes(new
            {
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                nodes = sessions.Bound.Count,
                bufferFillPercent = 0,
                version = typeof(MqttLineUplink).Assembly.GetName().Version?.ToString()
            }), ct);
            await Task.Delay(StatusInterval, ct);
        }

        if (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("connection lost");
        }
    }

    private Task PublishRetainedAsync(string topic, byte[] payload, CancellationToken ct) =>
        _client!.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithRetainFlag(true).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);

    private async Task OnDownlinkAsync(string topic, byte[] data, CancellationToken ct)
    {
        if (!GatewayTopics.TryParse(topic, out var address) || address.Kind != TopicKind.NodeDownlink || address.LineId != LineId)
        {
            return;
        }

        if (!WireFrameCodec.TryDecode(data, out var frame, out var error) || frame!.NodeId != address.NodeId)
        {
            logger.LogWarning("[Mqtt] bad downlink frame on {Topic}: {Error}", topic, error ?? "node id mismatch");
            return;
        }

        await router.RouteAsync(address.NodeId, frame, ct);
    }
}
