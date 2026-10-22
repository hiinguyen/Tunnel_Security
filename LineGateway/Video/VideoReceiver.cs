using System.Net;
using System.Net.Sockets;
using LineGateway.Gsan;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace LineGateway.Video;

/// <summary>IF-02: mảnh JPEG qua UDP; chỉ nhận từ IP của node đã gắn, mỗi IP một bộ ghép.</summary>
public sealed class VideoReceiver(
    IOptions<LineGatewayOptions> options,
    NodeSessionTable table,
    ICameraSink sink,
    ILogger<VideoReceiver> logger) : BackgroundService
{
    private readonly Dictionary<IPAddress, JpegFrameAssembler> _assemblers = new();
    private UdpClient? _udp;

    public int LocalPort => ((IPEndPoint)_udp!.Client.LocalEndPoint!).Port;

    public override Task StartAsync(CancellationToken ct)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, options.Value.VideoPort));
        _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;   // ~15 fps x nhiều node, tránh rơi gói khi worker bận
        logger.LogInformation("[Video] listening on udp/{Port}", LocalPort);
        return base.StartAsync(ct);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _udp?.Dispose();
        await base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _udp!.ReceiveAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                logger.LogDebug("[Video] receive error {Error}", ex.SocketErrorCode);
                continue;
            }

            var address = NodeSessionTable.Normalize(datagram.RemoteEndPoint.Address);
            if (!table.TryGetByAddress(address, out var session) || session.State != SessionState.Bound)
            {
                continue;
            }

            if (!_assemblers.TryGetValue(address, out var assembler))
            {
                if (_assemblers.Count >= options.Value.MaxConnections) _assemblers.Clear();
                _assemblers[address] = assembler = new JpegFrameAssembler();
            }

            if (assembler.TryAdd(datagram.Buffer, out var frame))
            {
                sink.PostFrame(session.NodeId, frame!.Jpeg);
            }
        }
    }
}
