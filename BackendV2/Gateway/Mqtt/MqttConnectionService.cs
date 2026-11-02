using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using WireProtocol;

namespace BackendV2.Gateway.Mqtt;

/// <summary>
/// Kết nối MQTT duy nhất của BackendV2: tự kết nối lại khi mất kết nối, subscribe cây topic <c>ts/...</c>,
/// chuyển bản tin vào <see cref="IMqttMessageHandler"/>, và cho các service khác publish qua <see cref="IMqttPublisher"/>.
/// Subscribe bằng wildcard nên tuyến/node mới tự động được nhận mà không cần cấu hình (docs mục 2.2).
/// Callback nhận của MQTTnet chỉ xếp bản tin vào hàng đợi; một worker riêng xử lý theo đúng thứ tự.
/// Nhờ vậy handler được phép chờ phản hồi từ broker (ví dụ Dynamic Security) mà không chặn chính vòng nhận phản hồi đó.
/// </summary>
public sealed class MqttConnectionService : BackgroundService, IMqttPublisher
{
    public const string DynSecResponseTopic = "$CONTROL/dynamic-security/v1/response";

    private readonly MqttOptions _options;
    // Lazy: handler phụ thuộc gián tiếp vào IMqttPublisher (chính service này), resolve ngay sẽ thành vòng DI.
    private readonly Lazy<IMqttMessageHandler> _handler;
    private readonly ILogger<MqttConnectionService> _logger;
    private readonly IMqttClient _client;
    private readonly List<Func<CancellationToken, Task>> _onConnected = [];
    private readonly ConcurrentDictionary<string, Action<ReadOnlyMemory<byte>>> _inlineHandlers = new();
    private const int ControlInboxCapacity = 10_000;

    // Hai hàng đợi: điều khiển (trạng thái NM, join, ack, cấp tuyến) luôn được xử lý trước dữ liệu A7,
    // để khi dữ liệu dồn dập thì bản tin điều khiển không bị bỏ cùng.
    private readonly Channel<(string Topic, byte[] Payload)> _dataInbox;
    private readonly Channel<(string Topic, byte[] Payload)> _controlInbox;
    private long _dropped;

    public MqttConnectionService(
        IOptions<MqttOptions> options, IServiceProvider services, ILogger<MqttConnectionService> logger)
    {
        _options = options.Value;
        _handler = new Lazy<IMqttMessageHandler>(services.GetRequiredService<IMqttMessageHandler>);
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _dataInbox = Channel.CreateBounded<(string, byte[])>(
            new BoundedChannelOptions(_options.InboxCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
        _controlInbox = Channel.CreateBounded<(string, byte[])>(
            new BoundedChannelOptions(ControlInboxCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Đăng ký việc cần làm mỗi lần (tái) kết nối, ví dụ bảo đảm tài khoản Dynamic Security.</summary>
    public void OnConnected(Func<CancellationToken, Task> callback) => _onConnected.Add(callback);

    /// <summary>
    /// Xử lý một topic ngay trong callback nhận, không qua hàng đợi. Chỉ dùng cho phản hồi mà worker đang chờ
    /// (nếu xếp hàng sau chính bản tin đang được xử lý thì sẽ tự khóa). Handler phải nhanh và không ném lỗi.
    /// </summary>
    public void HandleInline(string topic, Action<ReadOnlyMemory<byte>> handler) => _inlineHandlers[topic] = handler;

    public async Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, MqttQos qos, bool retain, CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            throw new InvalidOperationException($"MQTT not connected, cannot publish to {topic}");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToArray())
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .WithRetainFlag(retain)
            .Build();

        var result = await _client.PublishAsync(message, ct);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"MQTT publish to {topic} failed: {result.ReasonCode} {result.ReasonString}");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds);
        var worker = Task.Run(() => ProcessInboxAsync(stoppingToken), CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await ConnectAndSubscribeAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[MQTT] Connect to {Host}:{Port} failed: {Message}. Retrying in {Delay}s",
                    _options.Host, _options.Port, ex.Message, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }

        _dataInbox.Writer.TryComplete();
        _controlInbox.Writer.TryComplete();
        await worker;
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    private async Task ConnectAndSubscribeAsync(CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(_options.ClientId)
            .WithTcpServer(_options.Host, _options.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds))
            .WithCleanSession(true);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            builder = builder.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            builder = builder.WithTlsOptions(o => o.UseTls());
        }

        await _client.ConnectAsync(builder.Build(), ct);
        _logger.LogInformation("[MQTT] Connected to {Host}:{Port} as {ClientId}", _options.Host, _options.Port, _options.ClientId);

        try
        {
            await SetUpSessionAsync(ct);
        }
        catch
        {
            // Phiên chưa thiết lập xong (thiếu quyền hoặc subscription): ngắt để vòng lặp làm lại từ đầu,
            // thay vì giữ một kết nối "đã kết nối" nhưng không nhận được gì.
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }

            throw;
        }
    }

    private async Task SetUpSessionAsync(CancellationToken ct)
    {
        // Quyền của tài khoản (Dynamic Security) phải có trước khi subscribe, nếu không broker từ chối.
        foreach (var callback in _onConnected)
        {
            await callback(ct);
        }

        var dataFilter = string.IsNullOrEmpty(_options.SharedSubscriptionGroup)
            ? GatewayTopics.AllNodeUplinksFilter
            : $"$share/{_options.SharedSubscriptionGroup}/{GatewayTopics.AllNodeUplinksFilter}";

        var subscribe = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(dataFilter, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(GatewayTopics.AllGatewaysFilter, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(GatewayTopics.AllProvisionRequestsFilter, MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        var result = await _client.SubscribeAsync(subscribe, ct);
        var rejected = result.Items.Where(i => (int)i.ResultCode > (int)MqttClientSubscribeResultCode.GrantedQoS2).ToList();
        if (rejected.Count > 0)
        {
            throw new InvalidOperationException("Subscriptions rejected: " +
                string.Join(", ", rejected.Select(i => $"{i.TopicFilter.Topic} ({i.ResultCode})")));
        }

        _logger.LogInformation("[MQTT] Session ready, receiving gateway traffic");
    }

    /// <summary>Subscribe thêm sau khi đã kết nối (dùng cho topic phản hồi Dynamic Security).</summary>
    public async Task SubscribeAsync(string filter, CancellationToken ct)
    {
        var result = await _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter, MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
        var code = result.Items.First().ResultCode;
        if ((int)code > (int)MqttClientSubscribeResultCode.GrantedQoS2)
        {
            throw new InvalidOperationException($"Subscription to {filter} rejected: {code}");
        }
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.Payload.ToArray();

        if (_inlineHandlers.TryGetValue(topic, out var inline))
        {
            try
            {
                inline(payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MQTT] Inline handler for {Topic} failed", topic);
            }
        }
        else if (topic.EndsWith("/up/data", StringComparison.Ordinal))
        {
            _dataInbox.Writer.TryWrite((topic, payload));
        }
        else
        {
            _controlInbox.Writer.TryWrite((topic, payload));
        }

        return Task.CompletedTask;
    }

    private async Task ProcessInboxAsync(CancellationToken ct)
    {
        long reportedDrops = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_controlInbox.Reader.TryRead(out var message) && !_dataInbox.Reader.TryRead(out message))
                {
                    var control = _controlInbox.Reader.WaitToReadAsync(ct).AsTask();
                    var data = _dataInbox.Reader.WaitToReadAsync(ct).AsTask();
                    var ready = await Task.WhenAny(control, data);
                    if (!await ready && !await (ready == control ? data : control))
                    {
                        break; // Cả hai hàng đợi đã đóng (đang dừng service).
                    }

                    continue;
                }

                try
                {
                    await _handler.Value.HandleAsync(message.Topic, message.Payload, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "[MQTT] Unhandled error processing {Topic}", message.Topic);
                }

                var dropped = Interlocked.Read(ref _dropped);
                if (dropped != reportedDrops)
                {
                    _logger.LogWarning("[MQTT] Inbox full, {Count} messages dropped so far", dropped);
                    reportedDrops = dropped;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
