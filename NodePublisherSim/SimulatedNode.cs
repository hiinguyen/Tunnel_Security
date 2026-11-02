using System.Collections.Concurrent;
using WireProtocol;

namespace NodePublisherSim;

/// <summary>
/// Một node giả lập: giá trị đo dao động quanh mức bình thường và thỉnh thoảng có "sự kiện" kéo dài 20 giây
/// (có người trong hầm, nước dâng) để sinh cảnh báo giống thực tế thay vì giá trị ngẫu nhiên độc lập mỗi giây.
/// </summary>
public sealed class SimulatedNode
{
    private static readonly TimeSpan EventDuration = TimeSpan.FromSeconds(20);
    private const float LightBase = 120f;
    private const float LightEvent = 400f;

    private readonly Random _random;
    private readonly Dictionary<(ActuatorTypeByte, byte), bool> _actuators = new();
    private readonly DateTimeOffset _eventUntil = DateTimeOffset.MinValue; // su kien ngau nhien da tat
    private readonly float _waterBase;
    private readonly float _temperatureBase;
    private readonly float _humidityBase;
    private float _water;
    private float _temperature;
    private float _humidity;
    private float _light;
    private float _battery;

    public SimulatedNode(int index, uint physicalId, byte[] key, int seed)
    {
        Index = index;
        PhysicalId = physicalId;
        Key = key;
        _random = new Random(seed);
        _waterBase = _water = 0.3f + (float)_random.NextDouble() * 0.3f;
        _temperatureBase = _temperature = 24f + (float)_random.NextDouble() * 4f;
        _humidityBase = _humidity = 70f + (float)_random.NextDouble() * 10f;
        _light = LightBase;
        _battery = 85f + (float)_random.NextDouble() * 10f;
    }

    public int Index { get; }

    public uint PhysicalId { get; }

    public byte[] Key { get; }

    public byte? NodeId { get; set; }

    /// <summary>Tên hố ga trên bản đồ dashboard mà node này đóng vai (null khi dùng topology tổng hợp).</summary>
    public string? Name { get; set; }

    public NodePowerState Power { get; set; } = NodePowerState.On;

    public uint SampleIntervalMs { get; set; } = 1000;

    public DateTimeOffset NextJoinAttempt { get; set; }

    public DateTimeOffset NextData { get; set; }

    public DateTimeOffset NextKeepAlive { get; set; }

    public DateTimeOffset PausedUntil { get; set; }

    /// <summary>Gia tri ep cho tung loai cam bien (light/water/temp/hum/radar) do cong cu AlertInjector dat.</summary>
    private readonly ConcurrentDictionary<string, (float Value, DateTimeOffset Until)> _overrides = new();

    /// <summary>Ep cam bien <paramref name="kind"/> ve <paramref name="value"/> cho toi <paramref name="until"/>.</summary>
    public void Inject(string kind, float value, DateTimeOffset until) => _overrides[kind] = (value, until);

    /// <summary>Go gia tri ep cua mot cam bien, hoac cua moi cam bien neu <paramref name="kind"/> la null.</summary>
    public void ClearInjected(string? kind)
    {
        if (kind is null)
        {
            _overrides.Clear();
        }
        else
        {
            _overrides.TryRemove(kind, out _);
        }
    }

    public IReadOnlyList<(string Kind, float Value, DateTimeOffset Until)> ActiveOverrides(DateTimeOffset now) =>
        _overrides.Where(o => o.Value.Until > now).Select(o => (o.Key, o.Value.Value, o.Value.Until)).ToList();

    private float Forced(string kind, float natural, DateTimeOffset now)
    {
        if (!_overrides.TryGetValue(kind, out var o))
        {
            return natural;
        }

        if (o.Until <= now)
        {
            _overrides.TryRemove(kind, out _);
            return natural;
        }

        return o.Value + Noise(Math.Abs(o.Value) * 0.002f);
    }

    public bool InEvent(DateTimeOffset now) => now < _eventUntil;

    /// <summary>
    /// Su kien ngau nhien (nguoi/nuoc dang lam tang anh sang, muc nuoc, radar) da tat:
    /// gia tri chi vuot nguong khi AlertInjector ep. Giu ham de SimulatedGateway khong phai doi.
    /// </summary>
    public void MaybeStartEvent(DateTimeOffset now, double probability)
    {
    }

    public byte[] BuildSensorPayload(DateTimeOffset now)
    {
        var inEvent = InEvent(now);

        // Binh thuong cam bien gan nhu dung yen: nhieu rat nho + keo ve gia tri nen.
        // Chi khi co su kien (nguoi/nuoc dang) moi thay doi lon.
        _water = inEvent
            ? Math.Min(_water + 0.1f + Noise(0.01f), 3f)
            : Math.Max(_waterBase + (_water - _waterBase) * 0.95f + Noise(0.002f), 0.1f);
        _temperature = Math.Clamp(_temperature + (_temperatureBase - _temperature) * 0.02f + Noise(0.02f), 18f, 45f);
        _humidity = Math.Clamp(_humidity + (_humidityBase - _humidity) * 0.02f + Noise(0.1f), 0f, 100f);
        _light += ((inEvent ? LightEvent : LightBase) - _light) * 0.3f + Noise(0.5f);
        // Radar khong con tu sinh doi tuong ngau nhien: chi co gia tri khi AlertInjector ep (override "radar").
        var objects = _overrides.ContainsKey("radar") ? (int)Math.Round(Forced("radar", 0, now)) : 0;

        return SensorTlvCodec.Encode(
        [
            MetaTlv.Timestamp(now),
            new((byte)SensorTypeByte.Light, 1, new LightSensorValue(Forced("light", _light, now)).ToBytes()),
            new((byte)SensorTypeByte.WaterLevel, 1, new WaterLevelValue(Forced("water", _water, now)).ToBytes()),
            new((byte)SensorTypeByte.TemperatureHumidity, 1, new TemperatureHumidityValue(Forced("temp", _temperature, now), Forced("hum", _humidity, now)).ToBytes()),
            new((byte)SensorTypeByte.Radar, 1, new RadarValue(Enumerable.Range(0, objects).Select(_ => new RadarObject(
                Noise(5f), Noise(5f), 1f + Noise(0.5f), Math.Abs(Noise(1.5f)), 5f + Math.Abs(Noise(20f)), Math.Abs(Noise(3f)))).ToList()).ToBytes())
        ]);
    }

    public byte[] BuildKeepAlivePayload() => new KeepAliveStatus(
        BatteryPercent: _battery = Math.Clamp(_battery - 0.002f + Noise(0.02f), 0f, 100f),
        State: Power,
        Errors: NodeErrorFlags.None,
        Peripherals: _actuators.Select(a => new PeripheralState(
            a.Key.Item1 == ActuatorTypeByte.Speaker ? PeripheralKind.Horn : PeripheralKind.Light,
            (byte)Math.Min(a.Key.Item2, (byte)15), a.Value)).ToList()).ToBytes();

    /// <summary>Áp dụng payload A6; trả về false nếu payload không hợp lệ.</summary>
    public bool ApplyControl(byte[] payload)
    {
        var entries = SensorTlvCodec.Parse(payload);
        if (entries.Count == 0)
        {
            return false;
        }

        foreach (var e in entries)
        {
            if (e.TypeByte == MetaTlv.TypeByte && e.Seq == MetaTlv.PowerModeOrdinal && e.Value.Length == 1)
            {
                Power = (NodePowerState)e.Value[0];
            }
            else if (e.TypeByte >= 0x80 && e.Value.Length == 1)
            {
                _actuators[((ActuatorTypeByte)e.TypeByte, e.Seq)] = e.Value[0] != 0;
            }
        }

        return true;
    }

    private float Noise(float amplitude) => ((float)_random.NextDouble() * 2f - 1f) * amplitude;
}
