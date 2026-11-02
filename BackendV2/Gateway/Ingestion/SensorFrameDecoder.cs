using System.Text.Json;
using BackendV2.Models;
using WireProtocol;

namespace BackendV2.Gateway.Ingestion;

/// <summary>Một giá trị đo đã giải mã từ một TLV. Cảm biến nhiệt-ẩm sinh 2 giá trị ("temp" và "hum").</summary>
public sealed record DecodedMeasurement(
    string Kind, byte TypeByte, byte Ordinal, double Value, string Description, string? DetailJson);

public sealed record DecodedSensorFrame(
    DateTimeOffset MeasuredAt, bool HasDeviceTimestamp, IReadOnlyList<DecodedMeasurement> Measurements,
    IReadOnlyList<byte> UnknownTypeBytes, int NonFiniteValues);

/// <summary>Đặc tính mặc định của từng loại cảm biến, dùng khi tự tạo bản ghi Sensor lần đầu thấy giá trị.</summary>
public sealed record SensorKindProfile(
    string Kind, SensorType Type, string Name, string Unit, string Description, double? Warning, double? Critical);

/// <summary>
/// Giải mã payload A7 theo "Cấu trúc khung truyền". Thuần (không I/O) để kiểm thử độc lập.
/// Thời điểm đo lấy từ TLV 0x00 0x01 nếu có, chỉ khi nằm trong khoảng [giờ nhận - <see cref="MaxBacklog"/>,
/// giờ nhận + <see cref="MaxClockSkew"/>]; ngoài khoảng đó (đồng hồ node chưa đồng bộ) dùng giờ nhận.
/// Giá trị NaN/vô cực (cảm biến hỏng) bị loại, vì không lưu được vào CSDL và làm hỏng đường cảnh báo.
/// </summary>
public static class SensorFrameDecoder
{
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>NM đệm dữ liệu khi PC mất kết nối; bản đo cũ hơn mức này coi như đồng hồ sai.</summary>
    public static readonly TimeSpan MaxBacklog = TimeSpan.FromDays(30);

    public static readonly IReadOnlyDictionary<string, SensorKindProfile> Profiles = new Dictionary<string, SensorKindProfile>
    {
        ["light"] = new("light", SensorType.Light, "Cảm biến ánh sáng", "lx", "Cường độ ánh sáng (lx)", 750, 800),
        ["water"] = new("water", SensorType.WaterLevel, "Cảm biến mực nước", "m", "Độ sâu mực nước (m)", 1.5, 2.5),
        ["temp"] = new("temp", SensorType.Temperature, "Cảm biến nhiệt độ", "°C", "Nhiệt độ (°C)", 40, 50),
        ["hum"] = new("hum", SensorType.Humidity, "Cảm biến độ ẩm", "%", "Độ ẩm (%)", 95, 99),
        ["radar"] = new("radar", SensorType.Radar, "Cảm biến radar", "đối tượng", "Radar, số đối tượng phát hiện", 1, 3)
    };

    public static DecodedSensorFrame Decode(ReadOnlySpan<byte> payload, DateTimeOffset receivedAt)
    {
        var entries = SensorTlvCodec.Parse(payload);
        var measurements = new List<DecodedMeasurement>(entries.Count + 1);
        var unknown = new List<byte>();

        var hasTimestamp = MetaTlv.TryGetTimestamp(entries, out var deviceTime);
        var plausible = hasTimestamp && deviceTime <= receivedAt + MaxClockSkew && deviceTime >= receivedAt - MaxBacklog;
        var measuredAt = plausible ? deviceTime : receivedAt;

        foreach (var entry in entries)
        {
            switch (entry.TypeByte)
            {
                case MetaTlv.TypeByte:
                    break;

                case (byte)SensorTypeByte.Light when entry.Value.Length >= 4:
                    measurements.Add(Measure("light", entry, LightSensorValue.FromBytes(entry.Value).Lux));
                    break;

                case (byte)SensorTypeByte.WaterLevel when entry.Value.Length >= 4:
                    measurements.Add(Measure("water", entry, WaterLevelValue.FromBytes(entry.Value).DepthMeters));
                    break;

                case (byte)SensorTypeByte.TemperatureHumidity when entry.Value.Length >= 8:
                    var th = TemperatureHumidityValue.FromBytes(entry.Value);
                    measurements.Add(Measure("temp", entry, th.TemperatureC));
                    measurements.Add(Measure("hum", entry, th.HumidityPercent));
                    break;

                case (byte)SensorTypeByte.Radar when entry.Value.Length >= 1:
                    var radar = RadarValue.FromBytes(entry.Value);
                    measurements.Add(Measure("radar", entry, radar.Objects.Count, JsonSerializer.Serialize(radar.Objects)));
                    break;

                default:
                    unknown.Add(entry.TypeByte);
                    break;
            }
        }

        var finite = measurements.Where(m => double.IsFinite(m.Value)).ToList();
        return new DecodedSensorFrame(measuredAt, plausible, finite, unknown, measurements.Count - finite.Count);
    }

    private static DecodedMeasurement Measure(string kind, SensorTlvEntry entry, double value, string? detail = null) =>
        new(kind, entry.TypeByte, entry.Seq, value, Profiles[kind].Description, detail);
}
