using BackendV2.Gateway.Alerts;
using BackendV2.Gateway.Realtime;
using BackendV2.Models;

namespace BackendV2.Gateway.Ingestion;

/// <summary>
/// Xử lý khung A7 của một node đã tham gia: giải mã, phân loại theo ngưỡng, xếp hàng ghi CSDL,
/// phát realtime, và báo <see cref="AlertEngine"/> khi mức đã lọc dao động thay đổi. Không chạm CSDL trực tiếp
/// nên không làm chậm vòng nhận MQTT.
/// </summary>
public sealed class SensorIngestion
{
    private readonly SensorCatalog _catalog;
    private readonly ReadingWriter _writer;
    private readonly RealtimeNotifier _notifier;
    private readonly AlertEngine _alerts;
    private readonly SensorLevelTracker _levels;
    private readonly TimeProvider _time;
    private readonly ILogger<SensorIngestion> _logger;

    public SensorIngestion(
        SensorCatalog catalog,
        ReadingWriter writer,
        RealtimeNotifier notifier,
        AlertEngine alerts,
        SensorLevelTracker levels,
        TimeProvider time,
        ILogger<SensorIngestion> logger)
    {
        _catalog = catalog;
        _writer = writer;
        _notifier = notifier;
        _alerts = alerts;
        _levels = levels;
        _time = time;
        _logger = logger;
    }

    public async Task HandleAsync(NodeEntry node, byte[] payload)
    {
        var frame = SensorFrameDecoder.Decode(payload, _time.GetUtcNow());
        if (frame.UnknownTypeBytes.Count > 0)
        {
            _logger.LogDebug("[Ingest] {Node}: skipped unknown TLV types {Types}", node.NodeId,
                string.Join(",", frame.UnknownTypeBytes.Select(t => $"0x{t:X2}")));
        }

        if (frame.NonFiniteValues > 0)
        {
            _logger.LogWarning("[Ingest] {Node}: dropped {Count} NaN/Infinity value(s), sensor may be faulty",
                node.NodeId, frame.NonFiniteValues);
        }

        foreach (var m in frame.Measurements)
        {
            if (_catalog.Resolve(node, m) is not { } sensor)
            {
                _logger.LogDebug("[Ingest] {Node}: sensor limit {Max} reached, dropped {Kind}-{Ordinal}",
                    node.NodeId, SensorCatalog.MaxSensorsPerNode, m.Kind, m.Ordinal);
                continue;
            }
            var level = RuleEngine.Classify(m.Value, sensor.Warning, sensor.Critical);

            var reading = new Reading
            {
                SensorId = sensor.SensorId,
                NodeId = node.NodeId,
                LineId = (short)node.LineId,
                NodeByteId = node.NodeByteId,
                SensorByteId = m.TypeByte,
                Seq = m.Ordinal,
                Timestamp = frame.MeasuredAt.UtcDateTime,
                Value = m.Value,
                Description = m.DetailJson is null ? m.Description : $"{m.Description} | {m.DetailJson}",
                Level = level,
                Crc8Ok = true // Khung đã qua kiểm tra CRC ở WireFrameCodec.TryDecode.
            };

            _writer.Enqueue(reading);
            await _notifier.ReadingAsync(reading);

            if (_levels.Observe(sensor.SensorId, level) is { } changed)
            {
                var threshold = changed switch
                {
                    ReadingLevel.Critical => sensor.Critical,
                    ReadingLevel.Warning => sensor.Warning,
                    _ => null
                };
                _alerts.Post(new SensorLevelChanged(
                    sensor.SensorId, node.NodeId, node.LineId, node.NodeByteId, m.Kind, m.Ordinal,
                    changed, m.Value, threshold, frame.MeasuredAt));
            }
        }
    }
}
