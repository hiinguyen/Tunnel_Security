using BackendV2.Gateway.Ingestion;
using WireProtocol;

namespace BackendV2.Tests;

public class SensorFrameDecoderTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decodes_all_sensor_kinds_keeping_type_and_ordinal()
    {
        var payload = SensorTlvCodec.Encode(
        [
            new((byte)SensorTypeByte.Light, 1, new LightSensorValue(300f).ToBytes()),
            new((byte)SensorTypeByte.WaterLevel, 2, new WaterLevelValue(1.25f).ToBytes()),
            new((byte)SensorTypeByte.TemperatureHumidity, 1, new TemperatureHumidityValue(28f, 70f).ToBytes()),
            new((byte)SensorTypeByte.Radar, 1, new RadarValue([new RadarObject(1, 2, 0, 0.5f, 10, 1)]).ToBytes())
        ]);

        var frame = SensorFrameDecoder.Decode(payload, Received);

        Assert.Equal(
            ["light:1=300", "water:2=1.25", "temp:1=28", "hum:1=70", "radar:1=1"],
            frame.Measurements.Select(m => $"{m.Kind}:{m.Ordinal}={m.Value:0.##}"));
        Assert.Contains("\"DistanceM\":10", frame.Measurements.Last().DetailJson);
        Assert.Equal(Received, frame.MeasuredAt);
        Assert.False(frame.HasDeviceTimestamp);
    }

    [Fact]
    public void Uses_device_timestamp_when_present()
    {
        var measured = Received.AddMinutes(-30);
        var payload = SensorTlvCodec.Encode(
            [MetaTlv.Timestamp(measured), new((byte)SensorTypeByte.Light, 1, new LightSensorValue(1f).ToBytes())]);

        var frame = SensorFrameDecoder.Decode(payload, Received);

        Assert.True(frame.HasDeviceTimestamp);
        Assert.Equal(measured, frame.MeasuredAt);
        Assert.Single(frame.Measurements);
    }

    [Fact]
    public void Device_timestamp_far_in_the_future_falls_back_to_receive_time()
    {
        var payload = SensorTlvCodec.Encode([MetaTlv.Timestamp(Received.AddHours(2))]);

        Assert.Equal(Received, SensorFrameDecoder.Decode(payload, Received).MeasuredAt);
    }

    [Fact]
    public void Device_timestamp_too_far_in_the_past_falls_back_to_receive_time()
    {
        var payload = SensorTlvCodec.Encode([MetaTlv.Timestamp(DateTimeOffset.UnixEpoch)]);

        var frame = SensorFrameDecoder.Decode(payload, Received);

        Assert.Equal(Received, frame.MeasuredAt);
        Assert.False(frame.HasDeviceTimestamp);
    }

    [Fact]
    public void Non_finite_values_are_dropped_and_counted()
    {
        var payload = SensorTlvCodec.Encode(
        [
            new((byte)SensorTypeByte.Light, 1, new LightSensorValue(float.PositiveInfinity).ToBytes()),
            new((byte)SensorTypeByte.WaterLevel, 1, new WaterLevelValue(float.NaN).ToBytes()),
            new((byte)SensorTypeByte.TemperatureHumidity, 1, new TemperatureHumidityValue(25f, 60f).ToBytes())
        ]);

        var frame = SensorFrameDecoder.Decode(payload, Received);

        Assert.Equal(["temp", "hum"], frame.Measurements.Select(m => m.Kind));
        Assert.Equal(2, frame.NonFiniteValues);
    }

    [Fact]
    public void Unknown_and_truncated_entries_are_reported_not_thrown()
    {
        var payload = SensorTlvCodec.Encode(
        [
            new(0x7E, 1, [1, 2, 3]),
            new((byte)SensorTypeByte.WaterLevel, 1, [1, 2])
        ]);

        var frame = SensorFrameDecoder.Decode(payload, Received);

        Assert.Empty(frame.Measurements);
        Assert.Equal([(byte)0x7E, (byte)SensorTypeByte.WaterLevel], frame.UnknownTypeBytes);
    }
}
