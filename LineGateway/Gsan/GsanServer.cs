using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace LineGateway.Gsan;

/// <summary>IF-01: lắng nghe node trên TCP 9300, mỗi kết nối một <see cref="GsanSession"/>.</summary>
public sealed class GsanServer(
    IOptions<LineGatewayOptions> options,
    ILineUplink uplink,
    NodeSessionTable table,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    private readonly LineGatewayOptions _options = options.Value;
    private readonly ILogger _logger = loggers.CreateLogger<GsanServer>();
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConnections, options.Value.MaxConnections);
    private TcpListener? _listener;

    public int LocalPort => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    public override Task StartAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.IPv6Any, _options.GsanPort);
        _listener.Server.DualMode = true;
        _listener.Start();
        _logger.LogInformation("[Gsan] listening on {Port}", LocalPort);
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

            if (!_slots.Wait(0))
            {
                _logger.LogWarning("[Gsan] connection limit {Max} reached, refusing {Remote}", _options.MaxConnections, client.Client.RemoteEndPoint);
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var link = new TcpNodeLink(client);
        var session = new GsanSession(link, uplink, table, time, loggers.CreateLogger<GsanSession>());
        var reader = new FrameStreamReader(FrameFormat.Gsan);
        var buffer = new byte[4096];
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(_options.NodeIdleSeconds));
                var read = await stream.ReadAsync(buffer, idle.Token);
                if (read == 0) break;

                reader.Append(buffer.AsSpan(0, read));
                while (reader.TryRead(out var frame))
                {
                    await session.OnNodeFrameAsync(frame!, ct);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug("[Gsan] {Address}: connection ended ({Reason})", link.RemoteAddress, ex.GetType().Name);
        }
        finally
        {
            session.Close();
            _slots.Release();
        }
    }
}
