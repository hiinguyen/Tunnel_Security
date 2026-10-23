using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using BackendV2.Gateway.Alerts;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Mqtt;
using BackendV2.Gateway.Realtime;
using BackendV2.Models;
using Microsoft.Extensions.Options;

namespace BackendV2.Gateway.Uplink;

/// <summary>
/// Gửi 3 loại bản tin "Up link" lên Center (docs mục 6): An toàn, Cảnh báo, Tình huống, dạng JSON trên
/// <c>center/{stationId}/...</c>. Broker của PC bridge các topic này lên Center và tự đệm khi đường lên gián đoạn.
/// "An toàn" gửi khi trạng thái tổng đổi (kiểm tra mỗi giây) và định kỳ <see cref="StationOptions.SafetyIntervalSeconds"/>.
/// </summary>
public sealed class UplinkPublisher : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Payload MQTT, không nhúng vào HTML: giữ nguyên tiếng Việt thay vì escape \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IMqttPublisher _publisher;
    private readonly TopologyCache _topology;
    private readonly RealtimeNotifier _notifier;
    private readonly StationOptions _station;
    private readonly TimeProvider _time;
    private readonly ILogger<UplinkPublisher> _logger;

    // Lazy: AlertEngine và DeviceHealthService đều gửi bản tin qua service này, resolve ngay sẽ thành vòng DI.
    private readonly Lazy<AlertEngine> _alerts;
    private readonly Lazy<DeviceHealthService> _health;

    private readonly Channel<(string Topic, byte[] Payload, bool Retain)> _outbox =
        Channel.CreateBounded<(string, byte[], bool)>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    private (string Topic, byte[] Payload, bool Retain)? _inFlight;
    private volatile bool _safetyDirty = true;
    private string? _lastSafetyKey;
    private SafetyStatus? _lastSafety;

    public UplinkPublisher(
        IMqttPublisher publisher,
        TopologyCache topology,
        RealtimeNotifier notifier,
        IServiceProvider services,
        IOptions<StationOptions> station,
        TimeProvider time,
        ILogger<UplinkPublisher> logger)
    {
        _publisher = publisher;
        _topology = topology;
        _notifier = notifier;
        _station = station.Value;
        _time = time;
        _logger = logger;
        _alerts = new Lazy<AlertEngine>(services.GetRequiredService<AlertEngine>);
        _health = new Lazy<DeviceHealthService>(services.GetRequiredService<DeviceHealthService>);
    }

    public string SafetyTopic => $"center/{_station.Id}/safety";

    public string AlertTopic => $"center/{_station.Id}/alert";

    public string IncidentTopic => $"center/{_station.Id}/incident";

    public SafetyStatus? LastSafety => _lastSafety;

    public void RequestSafetyRefresh() => _safetyDirty = true;

    public void PublishAlert(Alert alert) => Enqueue(AlertTopic, new
    {
        alertId = alert.Id,
        stationId = _station.Id,
        ts = alert.UpdatedAt,
        state = alert.State,
        severity = alert.Severity,
        source = new { lineId = alert.LineId, nodeId = alert.NodeByteId, sensor = alert.SensorKind, seq = alert.SensorOrdinal },
        rule = alert.Rule,
        value = alert.Value,
        threshold = alert.Threshold,
        incidentId = alert.IncidentId,
        message = alert.Message
    }, retain: false);

    public void PublishIncident(Incident incident, IReadOnlyList<Guid> alertIds) => Enqueue(IncidentTopic, new
    {
        incidentId = incident.Id,
        stationId = _station.Id,
        ts = incident.UpdatedAt,
        state = incident.State,
        severity = incident.Severity,
        type = incident.Type,
        alertIds,
        location = new { lineId = incident.LineId, nodeIds = IncidentCorrelator.ParseNodes(incident.NodeByteIds) },
        @operator = incident.Operator,
        note = incident.Note
    }, retain: false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        var interval = TimeSpan.FromSeconds(_station.SafetyIntervalSeconds);
        var nextPeriodic = _time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushOutboxAsync(stoppingToken);

                var now = _time.GetUtcNow();
                if (_safetyDirty || now >= nextPeriodic)
                {
                    _safetyDirty = false;
                    if (await PublishSafetyAsync(force: now >= nextPeriodic, stoppingToken))
                    {
                        nextPeriodic = now + interval;
                    }
                }

                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Uplink] Loop failed");
            }
        }
    }

    private async Task<bool> PublishSafetyAsync(bool force, CancellationToken ct)
    {
        var status = SafetyCalculator.Compute(
            _station.Id, _time.GetUtcNow(), _topology.Lines, _health.Value.Gateways(), _health.Value.Nodes(), _alerts.Value.Snapshot);

        var key = JsonSerializer.Serialize(status with { Ts = default }, Json);
        var changed = key != _lastSafetyKey;
        if (!changed && !force)
        {
            return false;
        }

        _lastSafety = status;
        if (changed)
        {
            _lastSafetyKey = key;
            await _notifier.BroadcastAsync(RealtimeNotifier.SafetyStatus, status);
        }

        return await TryPublishAsync(SafetyTopic, JsonSerializer.SerializeToUtf8Bytes(status, Json), retain: true, ct);
    }

    private void Enqueue(string topic, object message, bool retain) =>
        _outbox.Writer.TryWrite((topic, JsonSerializer.SerializeToUtf8Bytes(message, Json), retain));

    private async Task FlushOutboxAsync(CancellationToken ct)
    {
        // Lấy hẳn bản tin ra khỏi hàng đợi và giữ ở _inFlight đến khi gửi được: nếu chỉ peek rồi đọc sau,
        // hàng đợi đầy (DropOldest) có thể đã bỏ đúng bản đang gửi và lần đọc sau xóa nhầm bản khác.
        while (true)
        {
            if (_inFlight is null)
            {
                if (!_outbox.Reader.TryRead(out var next))
                {
                    return;
                }

                _inFlight = next;
            }

            var item = _inFlight.Value;
            if (!await TryPublishAsync(item.Topic, item.Payload, item.Retain, ct))
            {
                return; // Thử lại ở lần lặp sau khi broker kết nối lại.
            }

            _inFlight = null;
        }
    }

    private async Task<bool> TryPublishAsync(string topic, byte[] payload, bool retain, CancellationToken ct)
    {
        try
        {
            await _publisher.PublishAsync(topic, payload, MqttQos.AtLeastOnce, retain, ct);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug("[Uplink] Publish to {Topic} deferred: {Message}", topic, ex.Message);
            return false;
        }
    }
}
