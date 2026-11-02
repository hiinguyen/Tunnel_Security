using System.Net;
using System.Net.Sockets;
using LineGateway.Gsan;
using LineGateway.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace LineGateway.Tests;

public class VideoReceiverTests
{
    private static byte[] Datagram(uint seq, ushort idx, ushort total, byte[] data)
    {
        var bytes = new byte[VideoChunkHeader.Size + data.Length];
        new VideoChunkHeader(seq, 1.0, idx, total).Write(bytes);
        data.CopyTo(bytes, VideoChunkHeader.Size);
        return bytes;
    }

    private static IOptions<LineGatewayOptions> Options0() =>
        Options.Create(new LineGatewayOptions { VideoPort = 0, MetaPort = 0, NodeIdleSeconds = 30 });

    [Fact]
    public async Task Udp_chunks_from_a_bound_node_become_one_frame_and_strangers_are_ignored()
    {
        var table = new NodeSessionTable();
        await TestSessions.BindAsync(table, "127.0.0.1", 7);
        var sink = new FakeSink();
        using var receiver = new VideoReceiver(Options0(), table, sink, NullLogger<VideoReceiver>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await receiver.StartAsync(cts.Token);

        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        await stranger.SendAsync(Datagram(1, 0, 1, [9]), new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), cts.Token);
        using var node = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
        await node.SendAsync(Datagram(5, 1, 2, [3, 4]), target, cts.Token);
        await node.SendAsync(Datagram(5, 0, 2, [1, 2]), target, cts.Token);

        while (sink.Frames.Count == 0) await Task.Delay(20, cts.Token);
        Assert.Single(sink.Frames);
        Assert.Equal(7, sink.Frames[0].Node);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.Frames[0].Jpeg);
        await receiver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Yolo_metadata_stream_is_parsed_into_viewer_json()
    {
        var table = new NodeSessionTable();
        await TestSessions.BindAsync(table, "127.0.0.1", 7);
        var sink = new FakeSink();
        using var receiver = new MetaReceiver(Options0(), table, sink, NullLogger<MetaReceiver>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await receiver.StartAsync(cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.LocalPort, cts.Token);
        var meta = new YoloMetadata(5, 1.0, [new YoloObject(0, 200, 10, 20, 110, 220, 3)]).ToBytes();
        var empty = new YoloMetadata(6, 1.1, []).ToBytes();
        await client.GetStream().WriteAsync(meta.Concat(empty).ToArray(), cts.Token);

        while (sink.Detections.Count < 2) await Task.Delay(20, cts.Token);
        Assert.All(sink.Detections, d => Assert.Equal(7, d.Node));
        Assert.Contains("\"seq\":5", sink.Detections[0].Json);
        Assert.Contains("\"objects\":[]", sink.Detections[1].Json);
        await receiver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Metadata_connection_from_unknown_address_is_closed()
    {
        var sink = new FakeSink();
        using var receiver = new MetaReceiver(Options0(), new NodeSessionTable(), sink, NullLogger<MetaReceiver>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await receiver.StartAsync(cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.LocalPort, cts.Token);

        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], cts.Token));
        await receiver.StopAsync(CancellationToken.None);
    }
}
