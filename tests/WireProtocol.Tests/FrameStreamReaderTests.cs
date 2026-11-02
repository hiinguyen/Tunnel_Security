using WireProtocol;

namespace WireProtocol.Tests;

public class FrameStreamReaderTests
{
    private static byte[] Frame(byte node) => WireFrameCodec.Encode(0xA7, node, [4, 1, 1, 0, 0], FrameFormat.Gsan);

    [Fact]
    public void Reads_frames_split_across_reads_and_glued_together()
    {
        var reader = new FrameStreamReader(FrameFormat.Gsan);
        var stream = Frame(1).Concat(Frame(2)).ToArray();

        reader.Append(stream.AsSpan(0, 7));
        Assert.False(reader.TryRead(out _));
        reader.Append(stream.AsSpan(7));

        Assert.True(reader.TryRead(out var a));
        Assert.True(reader.TryRead(out var b));
        Assert.False(reader.TryRead(out _));
        Assert.Equal(1, a!.NodeId);
        Assert.Equal(2, b!.NodeId);
    }

    [Fact]
    public void Resynchronises_after_garbage_and_corrupted_frame()
    {
        var reader = new FrameStreamReader(FrameFormat.Gsan);
        var corrupted = Frame(3);
        corrupted[^3] ^= 0xFF; // hỏng CRC

        reader.Append([0x00, 0x53, 0x11]);
        reader.Append(corrupted);
        reader.Append(Frame(4));

        Assert.True(reader.TryRead(out var frame));
        Assert.Equal(4, frame!.NodeId);
        Assert.True(reader.DiscardedBytes > 0);
    }

    [Fact]
    public void Oversized_length_field_does_not_stall_the_stream()
    {
        var reader = new FrameStreamReader(FrameFormat.Gsan, maxPayload: 64);
        reader.Append([0x53, 0xA7, 0xFF, 0xFF, 0x01]); // len 65535 > 64
        reader.Append(Frame(5));

        Assert.True(reader.TryRead(out var frame));
        Assert.Equal(5, frame!.NodeId);
    }
}
