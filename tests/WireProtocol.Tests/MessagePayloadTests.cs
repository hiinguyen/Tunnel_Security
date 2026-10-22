using System.Net;
using System.Security.Cryptography;
using WireProtocol;

namespace WireProtocol.Tests;

public class MetaTlvTests
{
    [Fact]
    public void Timestamp_entry_round_trips_with_millisecond_precision()
    {
        var at = new DateTimeOffset(2026, 10, 14, 8, 1, 12, 345, TimeSpan.Zero);

        var entry = MetaTlv.Timestamp(at);

        Assert.Equal(MetaTlv.TypeByte, entry.TypeByte);
        Assert.Equal(MetaTlv.TimestampOrdinal, entry.Seq);
        Assert.Equal(8, entry.Value.Length);
        Assert.True(MetaTlv.TryGetTimestamp([entry], out var decoded));
        Assert.Equal(at, decoded);
    }

    [Fact]
    public void Timestamp_survives_full_frame_round_trip_next_to_sensor_entries()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_123);
        var payload = SensorTlvCodec.Encode(
        [
            MetaTlv.Timestamp(at),
            new((byte)SensorTypeByte.Light, 1, new LightSensorValue(120f).ToBytes())
        ]);
        var frame = WireFrameCodec.Encode((byte)NodeCommand.SensorData, 4, payload);

        Assert.True(WireFrameCodec.TryDecode(frame, out var decoded, out _));
        var entries = SensorTlvCodec.Parse(decoded!.Payload);

        Assert.True(MetaTlv.TryGetTimestamp(entries, out var ts));
        Assert.Equal(at, ts);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Missing_or_truncated_timestamp_is_reported_as_absent()
    {
        Assert.False(MetaTlv.TryGetTimestamp([], out _));
        Assert.False(MetaTlv.TryGetTimestamp([new(MetaTlv.TypeByte, MetaTlv.TimestampOrdinal, new byte[3])], out _));
    }
}

public class JoinProtocolTests
{
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

    [Fact]
    public void Request_and_response_are_distinguished_by_length()
    {
        Assert.True(JoinProtocol.TryDecodeUplink(JoinProtocol.EncodeRequest(0xDEADBEEF), out var request));
        Assert.Equal(0xDEADBEEFu, request.PhysicalId);
        Assert.Null(request.Mac);

        Assert.True(JoinProtocol.TryDecodeUplink(JoinProtocol.EncodeResponse(0xDEADBEEF, 0x01020304), out var response));
        Assert.Equal(0xDEADBEEFu, response.PhysicalId);
        Assert.Equal(0x01020304u, response.Mac);

        Assert.False(JoinProtocol.TryDecodeUplink(new byte[5], out _));
    }

    [Fact]
    public void Challenge_round_trips()
    {
        var payload = JoinProtocol.EncodeChallenge(42, 0xCAFEBABE);

        Assert.True(JoinProtocol.TryDecodeChallenge(payload, out var physicalId, out var nonce));
        Assert.Equal(42u, physicalId);
        Assert.Equal(0xCAFEBABEu, nonce);
    }

    [Fact]
    public void Derived_device_keys_are_deterministic_and_distinct_per_device()
    {
        var a = JoinProtocol.DeriveDeviceKey(Key, 1);

        Assert.Equal(32, a.Length);
        Assert.Equal(a, JoinProtocol.DeriveDeviceKey(Key, 1));
        Assert.NotEqual(a, JoinProtocol.DeriveDeviceKey(Key, 2));
        Assert.Equal(HMACSHA256.HashData(Key, new byte[] { 1, 0, 0, 0 }), a);
    }
}

public class NodeAssignmentTests
{
    [Fact]
    public void Assignment_round_trips_with_config()
    {
        var config = new NodeConfig(NodeId: 9, SampleIntervalMs: 1000, NodeType: 2, IpAddress: IPAddress.Parse("10.3.1.9"));

        var payload = NodeAssignment.Encode(0xA1B2C3D4, 9, config);

        Assert.True(NodeAssignment.TryDecode(payload, out var physicalId, out var nodeId, out var decoded));
        Assert.Equal(0xA1B2C3D4u, physicalId);
        Assert.Equal(9, nodeId);
        Assert.Equal(config, decoded);
    }

    [Fact]
    public void Confirm_round_trips()
    {
        Assert.True(NodeAssignment.TryDecodeConfirm(
            NodeAssignment.EncodeConfirm(77, CommandResultCode.Ok), out var physicalId, out var result));
        Assert.Equal(77u, physicalId);
        Assert.Equal(CommandResultCode.Ok, result);
    }
}

public class NodeConfigTests
{
    [Fact]
    public void Partial_config_only_encodes_present_fields()
    {
        var config = new NodeConfig(SampleIntervalMs: 500);

        var entries = SensorTlvCodec.Parse(config.ToBytes());

        Assert.Single(entries);
        Assert.Equal(config, NodeConfig.FromEntries(entries));
    }

    [Fact]
    public void Unknown_entries_are_ignored()
    {
        var bytes = SensorTlvCodec.Encode([new(0x00, 0x7E, new byte[] { 1 }), new(0x00, NodeConfig.NodeTypeOrdinal, new byte[] { 3 })]);

        Assert.Equal(new NodeConfig(NodeType: 3), NodeConfig.FromEntries(SensorTlvCodec.Parse(bytes)));
    }
}

public class KeepAliveStatusTests
{
    [Fact]
    public void Round_trips_with_peripherals()
    {
        var status = new KeepAliveStatus(
            87.5f,
            NodePowerState.On,
            NodeErrorFlags.LowBattery | NodeErrorFlags.Rs485Fault,
            [new(PeripheralKind.Light, 0, true), new(PeripheralKind.Horn, 15, false)]);

        var bytes = status.ToBytes();

        Assert.Equal(6 + 2 * 2, bytes.Length);
        Assert.Equal(0xA0, bytes[6]);
        Assert.Equal(0xBF, bytes[8]);
        Assert.True(KeepAliveStatus.TryParse(bytes, out var decoded));
        Assert.Equal(status.BatteryPercent, decoded!.BatteryPercent);
        Assert.Equal(status.State, decoded.State);
        Assert.Equal(status.Errors, decoded.Errors);
        Assert.Equal(status.Peripherals, decoded.Peripherals);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void Rejects_bad_length(int length)
    {
        Assert.False(KeepAliveStatus.TryParse(new byte[length], out _));
    }
}

public class ControlPayloadTests
{
    [Fact]
    public void Builds_power_mode_and_actuator_entries()
    {
        var bytes = SensorTlvCodec.Encode(
        [
            ControlPayload.PowerMode(NodePowerState.Sleep),
            ControlPayload.Actuator(ActuatorTypeByte.Speaker, 2, true)
        ]);

        var entries = SensorTlvCodec.Parse(bytes);

        Assert.Equal(new byte[] { (byte)NodePowerState.Sleep }, entries[0].Value);
        Assert.Equal(MetaTlv.PowerModeOrdinal, entries[0].Seq);
        Assert.Equal((byte)ActuatorTypeByte.Speaker, entries[1].TypeByte);
        Assert.Equal(2, entries[1].Seq);
        Assert.Equal(new byte[] { 1 }, entries[1].Value);
    }

    [Fact]
    public void Ack_round_trips()
    {
        Assert.True(CommandAck.TryDecode(CommandAck.Encode(CommandResultCode.Busy), out var code));
        Assert.Equal(CommandResultCode.Busy, code);
        Assert.False(CommandAck.TryDecode([], out _));
    }
}
