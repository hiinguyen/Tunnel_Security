using System.Net;
using System.Net.Sockets;
using LineGateway.Gsan;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace LineGateway.Video;

/// <summary>IF-03: metadata YOLO qua TCP; kết nối từ địa chỉ chưa gắn bị đóng ngay.</summary>
public sealed class MetaReceiver(
    IOptions<LineGatewayOptions> options,
    NodeSessionTable table,
    ICameraSink sink,
    ILogger<MetaReceiver> logger) : BackgroundService
{
    private TcpListener? _listener;

    public int LocalPort => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    public override Task StartAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Any, options.Value.MetaPort);
        _listener.Start();
        logger.LogInformation("[Meta] listening on tcp/{Port}", LocalPort);
        return base.StartAsync(ct);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _listener?.Stop();
        await base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }

            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var address = NodeSessionTable.Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
        if (!table.TryGetByAddress(address, out var session) || session.State != SessionState.Bound)
        {
            logger.LogDebug("[Meta] refusing {Address}: no bound node", address);
            return;
        }

        var header = new byte[YoloMetadata.HeaderSize];
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested && session.State == SessionState.Bound)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(options.Value.NodeIdleSeconds));
                await stream.ReadExactlyAsync(header, idle.Token);
                var count = YoloMetadata.ObjectCount(header);
                if (count < 0)
                {
                    logger.LogWarning("[Meta] node {Node}: object count over limit, closing", session.NodeId);
                    return;
                }

                var message = new byte[YoloMetadata.HeaderSize + count * YoloMetadata.ObjectSize];
                header.CopyTo(message, 0);
                await stream.ReadExactlyAsync(message.AsMemory(YoloMetadata.HeaderSize), idle.Token);
                if (YoloMetadata.TryParse(message, out var meta))
                {
                    sink.PostDetections(session.NodeId, meta!.ToJson());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException)
        {
            logger.LogDebug("[Meta] node {Node}: stream ended ({Reason})", session.NodeId, ex.GetType().Name);
        }
    }
}
