using System.Text;
using System.Text.Json;
using WireProtocol;

namespace WireProtocol.Tests;

public class GsanFrameTests
{
    internal static readonly JsonElement Vectors =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gsan.json"))).RootElement;

    [Fact]
    public void Modbus_crc_matches_standard_check_value()
    {
        var check = Vectors.GetProperty("crcModbusCheck");
        Assert.Equal(check.GetProperty("crc").GetUInt16(),
            Crc16Modbus.Compute(Encoding.ASCII.GetBytes(check.GetProperty("ascii").GetString()!)));
    }

    public static TheoryData<string, byte, byte, string, string> Frames()
    {
        var data = new TheoryData<string, byte, byte, string, string>();
        foreach (var f in Vectors.GetProperty("frames").EnumerateArray())
        {
            data.Add(f.GetProperty("name").GetString()!, f.GetProperty("cmd").GetByte(), f.GetProperty("node").GetByte(),
                f.GetProperty("payload").GetString()!, f.GetProperty("hex").GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void Gsan_frames_match_vectors_both_ways(string name, byte cmd, byte node, string payloadHex, string frameHex)
    {
        var payload = Convert.FromHexString(payloadHex);

        Assert.Equal(frameHex, Convert.ToHexString(WireFrameCodec.Encode(cmd, node, payload, FrameFormat.Gsan)).ToLowerInvariant());
        Assert.True(WireFrameCodec.TryDecode(Convert.FromHexString(frameHex), out var frame, out var error, FrameFormat.Gsan), $"{name}: {error}");
        Assert.Equal(cmd, frame!.Command);
        Assert.Equal(node, frame.NodeId);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public void Gsan_frame_is_rejected_by_lan_format_and_vice_versa()
    {
        var gsan = WireFrameCodec.Encode(0xA7, 7, [4, 1, 1, 0, 0], FrameFormat.Gsan);
        var lan = WireFrameCodec.Encode(0xA7, 7, [4, 1, 1, 0, 0]);

        Assert.False(WireFrameCodec.TryDecode(gsan, out _, out _));
        Assert.False(WireFrameCodec.TryDecode(lan, out _, out _, FrameFormat.Gsan));
    }
}
