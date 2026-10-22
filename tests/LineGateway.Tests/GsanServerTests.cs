using System.Net.Sockets;
using LineGateway.Gsan;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireProtocol;

namespace LineGateway.Tests;

public class GsanServerTests
{
    [Fact]
    public async Task Join_request_over_tcp_reaches_the_uplink_and_disconnect_frees_the_session()
    {
        var uplink = new FakeUplink();
        var table = new NodeSessionTable();
        var options = Options.Create(new LineGatewayOptions { GsanPort = 0, NodeIdleSeconds = 30 });
        using var server = new GsanServer(options, uplink, table, TimeProvider.System, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.StartAsync(cts.Token);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", server.LocalPort, cts.Token);
            var request = WireFrameCodec.Encode(0xA1, 0, GsanJoin.EncodeRequest(0x12345678), FrameFormat.Gsan);
            await client.GetStream().WriteAsync(request.AsMemory(0, 3), cts.Token);   // gửi tách đôi khung
            await client.GetStream().WriteAsync(request.AsMemory(3), cts.Token);

            while (uplink.Published.Count == 0) await Task.Delay(20, cts.Token);
            Assert.True(table.TryGetPending(0x12345678, out _));
        }

        while (table.TryGetPending(0x12345678, out _)) await Task.Delay(20, cts.Token);
        Assert.Equal(JoinProtocol.EncodeRequest(0x12345678), uplink.Published[0].Payload);
        await server.StopAsync(CancellationToken.None);
    }
}
