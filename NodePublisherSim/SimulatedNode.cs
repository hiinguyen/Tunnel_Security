using WireProtocol;

namespace NodePublisherSim;

/// <summary>
/// Một node giả lập: giá trị đo dao động quanh mức bình thường và thỉnh thoảng có "sự kiện" kéo dài 20 giây
/// (có người trong hầm, nước dâng) để sinh cảnh báo giống thực tế thay vì giá trị ngẫu nhiên độc lập mỗi giây.
/// </summary>
public sealed class SimulatedNode
{
    private static readonly TimeSpan EventDuration = TimeSpan.FromSeconds(20);

    private readonly Random _random;
    private readonly Dictionary<(ActuatorTypeByte, byte), bool> _actuators = new();
    private DateTimeOffset _eventUntil;
    private float _water;
    private float _temperature;

    public SimulatedNode(int index, uint physicalId, byte[] key, int seed)
    {
        Index = index;
        PhysicalId = physicalId;
        Key = key;
        _random = new Random(seed);
        _water = 0.3f + (float)_random.NextDouble() * 0.3f;
        _temperature = 24f + (float)_random.NextDouble() * 4f;
    }

    public int Index { get; }

    public uint PhysicalId { get; }

    public byte[] Key { get; }

    public byte? NodeId { get; set; }

    public NodePowerState Power { get; set; } = NodePowerState.On;

    public uint SampleIntervalMs { get; set; } = 1000;

    public DateTimeOffset NextJoinAttempt { get; set; }

    public DateTimeOffset NextData { get; set; }

    public DateTimeOffset NextKeepAlive { get; set; }

    public DateTimeOffset PausedUntil { get; set; }

    public bool InEvent(DateTimeOffset now) => now < _eventUntil;

    public void MaybeStartEvent(DateTimeOffset now, double probability)
    {
        if (!InEvent(now) && _random.NextDouble() < probability)
        {
            _eventUntil = now + EventDuration;
        }
    }

    public byte[] BuildSensorPayload(DateTimeOffset now)
    {
        var inEvent = InEvent(now);
        _water = Math.Clamp(_water + (inEvent ? 0.15f : -0.05f) + Noise(0.02f), 0.1f, 3f);
        _temperature = Math.Clamp(_temperature + Noise(0.1f), 18f, 45f);
        var objects = inEvent ? _random.Next(1, 4) : 0;

        return SensorTlvCodec.Encode(
        [
            MetaTlv.Timestamp(now),
            new((byte)SensorTypeByte.Light, 1, new LightSensorValue(inEvent ? 400f + Noise(50f) : 120f + Noise(10f)).ToBytes()),
            new((byte)SensorTypeByte.WaterLevel, 1, new WaterLevelValue(_water).ToBytes()),
            new((byte)SensorTypeByte.TemperatureHumidity, 1, new TemperatureHumidityValue(_temperature, 75f + Noise(3f)).ToBytes()),
            new((byte)SensorTypeByte.Radar, 1, new RadarValue(Enumerable.Range(0, objects).Select(_ => new RadarObject(
                Noise(5f), Noise(5f), 1f + Noise(0.5f), Math.Abs(Noise(1.5f)), 5f + Math.Abs(Noise(20f)), Math.Abs(Noise(3f)))).ToList()).ToBytes())
        ]);
    }

    public byte[] BuildKeepAlivePayload() => new KeepAliveStatus(
        BatteryPercent: 80f + Noise(5f),
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
