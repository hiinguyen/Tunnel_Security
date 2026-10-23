using WireProtocol;

namespace WireProtocol.Tests;

public class GsanMessagesTests
{
    private static readonly System.Text.Json.JsonElement V = GsanFrameTests.Vectors;

    [Fact]
    public void Mac_is_hmac_sha256_of_nonce_truncated_to_four_bytes_as_in_sdd()
    {
        var key = Convert.FromHexString(V.GetProperty("deviceKeyHex").GetString()!);
        var nonce = V.GetProperty("joinMac").GetProperty("nonce").GetUInt32();
        var macBytes = Convert.FromHexString(V.GetProperty("joinMac").GetProperty("macHex").GetString()!);
        var mac = BitConverter.ToUInt32(macBytes);

        Assert.Equal(mac, JoinProtocol.ComputeMac(key, nonce));
        Assert.True(JoinProtocol.VerifyMac(key, nonce, mac));
        Assert.False(JoinProtocol.VerifyMac(key, nonce, mac ^ 1));
        Assert.True(GsanJoin.TryDecodeResponse(macBytes, out var decoded));
        Assert.Equal(mac, decoded);
    }

    [Fact]
    public void Join_payloads_round_trip()
    {
        Assert.True(GsanJoin.TryDecodeRequest(GsanJoin.EncodeRequest(0x12345678), out var phys));
        Assert.Equal(0x12345678u, phys);
        Assert.Equal(Convert.FromHexString("bebafeca"), GsanJoin.EncodeChallenge(0xCAFEBABE));
        Assert.Equal(new byte[] { 7 }, GsanJoin.EncodeAssignment(7));
        Assert.True(GsanJoin.IsAssignmentOk("OK"u8));
        Assert.False(GsanJoin.IsAssignmentOk("NO"u8));
        Assert.False(GsanJoin.TryDecodeRequest([1, 2, 3], out _));
    }

    [Fact]
    public void Heartbeat_matches_vector_and_maps_to_keepalive()
    {
        var hb = V.GetProperty("heartbeat");
        var frame = Convert.FromHexString(hb.GetProperty("hex").GetString()!);
        Assert.True(WireFrameCodec.TryDecode(frame, out var decoded, out _, FrameFormat.Gsan));

        Assert.True(GsanHeartbeat.TryParse(decoded!.Payload, out var parsed));
        Assert.Equal(new GsanHeartbeat(2, 0, GsanSensorFlags.Radar | GsanSensorFlags.Camera, 48.5f), parsed);
        Assert.Equal(decoded.Payload, parsed.ToBytes());

        var keepAlive = parsed.ToKeepAlive();
        Assert.Equal(NodePowerState.On, keepAlive.State);
        Assert.Equal(NodeErrorFlags.None, keepAlive.Errors);
        Assert.Null(keepAlive.KnownBatteryPercent);
    }

    [Fact]
    public void Missing_radar_or_camera_is_reported_as_sensor_fault()
    {
        var keepAlive = new GsanHeartbeat(2, 0, GsanSensorFlags.Camera, 40f).ToKeepAlive();

        Assert.True(keepAlive.Errors.HasFlag(NodeErrorFlags.SensorFault));
    }
}
