using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace LineGateway.Video;

/// <summary>Mỗi node một <see cref="CameraForwarder"/>, tạo khi có frame đầu tiên; camera id theo lan doc 5.4.</summary>
public sealed class CameraForwarderPool(
    IOptions<LineGatewayOptions> options,
    ILineIdentity line,
    ILoggerFactory loggers) : ICameraSink, IAsyncDisposable
{
    private readonly ConcurrentDictionary<byte, (CameraForwarder Forwarder, Task Run)> _forwarders = new();
    private readonly CancellationTokenSource _stopping = new();

    public void PostFrame(byte nodeId, byte[] jpeg) => Post(nodeId, WebSocketMessageType.Binary, jpeg);

    public void PostDetections(byte nodeId, string json) => Post(nodeId, WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

    private void Post(byte nodeId, WebSocketMessageType type, byte[] data)
    {
        if (line.LineId is not { } lineId)
        {
            return;   // chưa được cấp tuyến: chưa có camera id
        }

        while (true)
        {
            var entry = _forwarders.GetOrAdd(nodeId, n => Start(lineId, n));
            if (!entry.Run.IsCompleted && entry.Forwarder.TryPost(type, data))
            {
                return;
            }

            _forwarders.TryRemove(new KeyValuePair<byte, (CameraForwarder, Task)>(nodeId, entry));
        }
    }

    private (CameraForwarder, Task) Start(int lineId, byte nodeId)
    {
        var uri = new Uri($"{options.Value.CameraIngestBaseUrl.TrimEnd('/')}/CAM-L{lineId}-N{nodeId}/ingest");
        var forwarder = new CameraForwarder(uri, loggers.CreateLogger<CameraForwarder>());
        return (forwarder, Task.Run(() => forwarder.RunAsync(_stopping.Token)));
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        foreach (var (forwarder, run) in _forwarders.Values)
        {
            forwarder.Complete();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
