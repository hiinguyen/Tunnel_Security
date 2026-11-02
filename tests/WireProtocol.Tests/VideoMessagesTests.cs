using System.Text.Json;
using WireProtocol;

namespace WireProtocol.Tests;

public class VideoMessagesTests
{
    private static readonly JsonElement V = GsanFrameTests.Vectors;

    private static byte[] Datagram(uint seq, ushort idx, ushort total, byte[] data)
    {
        var bytes = new byte[VideoChunkHeader.Size + data.Length];
        new VideoChunkHeader(seq, 1.5, idx, total).Write(bytes);
        data.CopyTo(bytes, VideoChunkHeader.Size);
        return bytes;
    }

    [Fact]
    public void Chunk_header_matches_vector()
    {
        var v = V.GetProperty("videoChunkHeader");
        var bytes = new byte[VideoChunkHeader.Size];
        new VideoChunkHeader(v.GetProperty("seq").GetUInt32(), v.GetProperty("ts").GetDouble(),
            v.GetProperty("idx").GetUInt16(), v.GetProperty("total").GetUInt16()).Write(bytes);

        Assert.Equal(v.GetProperty("hex").GetString(), Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void Assembles_out_of_order_chunks_into_one_jpeg()
    {
        var assembler = new JpegFrameAssembler();

        Assert.False(assembler.TryAdd(Datagram(9, 1, 2, [3, 4]), out _));
        Assert.True(assembler.TryAdd(Datagram(9, 0, 2, [1, 2]), out var frame));
        Assert.Equal(9u, frame!.Seq);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame.Jpeg);
    }

    [Fact]
    public void Newer_frame_drops_incomplete_older_one_and_late_chunks_are_ignored()
    {
        var assembler = new JpegFrameAssembler();

        Assert.False(assembler.TryAdd(Datagram(1, 0, 2, [1]), out _));
        Assert.True(assembler.TryAdd(Datagram(2, 0, 1, [9]), out var two));
        Assert.Equal(2u, two!.Seq);
        Assert.False(assembler.TryAdd(Datagram(1, 1, 2, [2]), out _));
    }

    [Fact]
    public void Rejects_malformed_datagrams()
    {
        var assembler = new JpegFrameAssembler();

        Assert.False(assembler.TryAdd([1, 2, 3], out _));
        Assert.False(assembler.TryAdd(Datagram(1, 2, 2, [1]), out _));
        Assert.False(assembler.TryAdd(Datagram(1, 0, 0, [1]), out _));
        Assert.False(assembler.TryAdd(Datagram(1, 0, JpegFrameAssembler.MaxChunks + 1, [1]), out _));
        Assert.False(assembler.TryAdd(Datagram(1, 0, 1, new byte[VideoChunkHeader.MaxChunkData + 1]), out _));
    }

    [Fact]
    public void Yolo_metadata_matches_vector_and_serialises_for_viewers()
    {
        var v = V.GetProperty("yoloMeta");
        var bytes = Convert.FromHexString(v.GetProperty("hex").GetString()!);

        Assert.Equal(1, YoloMetadata.ObjectCount(bytes.AsSpan(0, YoloMetadata.HeaderSize)));
        Assert.True(YoloMetadata.TryParse(bytes, out var meta));
        Assert.Equal(new YoloObject(0, 200, 10, 20, 110, 220, 5), meta!.Objects[0]);
        Assert.Equal(bytes, meta.ToBytes());

        using var json = JsonDocument.Parse(meta.ToJson());
        Assert.Equal("detections", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(110, json.RootElement.GetProperty("objects")[0].GetProperty("box")[2].GetInt32());
        Assert.Equal(200 / 255.0, json.RootElement.GetProperty("objects")[0].GetProperty("conf").GetDouble(), 3);
    }

    [Fact]
    public void Yolo_metadata_with_too_many_objects_is_rejected()
    {
        var header = new byte[YoloMetadata.HeaderSize];
        header[12] = 0xFF;
        header[13] = 0xFF;

        Assert.Equal(-1, YoloMetadata.ObjectCount(header));
    }
}
