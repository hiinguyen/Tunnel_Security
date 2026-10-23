using System.Net.WebSockets;
using BackendV2.Services;

namespace BackendV2.Tests;

public class CameraRelayRegistryTests
{
    private sealed class RecordingSocket : WebSocket
    {
        private int _inFlight;
        public int MaxConcurrentSends { get; private set; }
        public List<(WebSocketMessageType Type, byte[] Data)> Sent { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) => throw new NotSupportedException();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrentSends = Math.Max(MaxConcurrentSends, now);
            await Task.Delay(5, ct);
            lock (Sent) Sent.Add((type, buffer.ToArray()));
            Interlocked.Decrement(ref _inFlight);
        }
    }

    [Fact]
    public async Task Text_messages_are_relayed_but_do_not_replace_latest_frame()
    {
        var registry = new CameraRelayRegistry();
        var viewer = new RecordingSocket();
        registry.AddViewer("CAM-L1-N1", viewer);

        await registry.BroadcastAsync("CAM-L1-N1", [0xFF, 0xD8], WebSocketMessageType.Binary, CancellationToken.None);
        await registry.BroadcastAsync("CAM-L1-N1", "{\"type\":\"detections\"}"u8.ToArray(), WebSocketMessageType.Text, CancellationToken.None);

        Assert.Equal([WebSocketMessageType.Binary, WebSocketMessageType.Text], viewer.Sent.Select(s => s.Type));
        Assert.True(registry.TryGetLatestFrame("CAM-L1-N1", out var latest));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, latest);
    }

    [Fact]
    public async Task Sends_to_one_viewer_never_overlap()
    {
        var registry = new CameraRelayRegistry();
        var viewer = new RecordingSocket();
        registry.AddViewer("CAM-L1-N1", viewer);
        await registry.BroadcastAsync("CAM-L1-N1", [1], WebSocketMessageType.Binary, CancellationToken.None);

        await Task.WhenAll(
            registry.SendLatestFrameAsync("CAM-L1-N1", viewer, CancellationToken.None),
            registry.BroadcastAsync("CAM-L1-N1", [2], WebSocketMessageType.Binary, CancellationToken.None),
            registry.BroadcastAsync("CAM-L1-N1", "{}"u8.ToArray(), WebSocketMessageType.Text, CancellationToken.None));

        Assert.Equal(1, viewer.MaxConcurrentSends);
        Assert.Equal(4, viewer.Sent.Count);
    }
}
