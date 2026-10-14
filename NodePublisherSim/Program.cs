using NodePublisherSim;

// Giả lập LINES NM, mỗi NM có NODES_PER_LINE node, theo giao thức PC - NM (docs/lan-architecture-protocol.md).
// Ví dụ kiểm thử tải: LINES=30 NODES_PER_LINE=30 dotnet run --project NodePublisherSim
var options = SimOptions.FromEnvironment();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Console.WriteLine($"[NodePublisherSim] {options.Lines} line(s) x {options.NodesPerLine} node(s) -> {options.BrokerHost}:{options.BrokerPort}");

var gateways = Enumerable.Range(options.FirstGatewayIndex, options.Lines).Select(i => new SimulatedGateway(options, i)).ToList();
var runs = gateways.Select(g => g.RunAsync(cts.Token)).ToList();

var report = Task.Run(async () =>
{
    long last = 0;
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(_ => { });
        var total = gateways.Sum(g => g.Published);
        Console.WriteLine(
            $"[NodePublisherSim] joined {gateways.Sum(g => g.JoinedNodes)}/{options.Lines * options.NodesPerLine} nodes, " +
            $"{(total - last) / 10.0:0.#} msg/s");
        last = total;
    }
});

await Task.WhenAll(runs);
await report;
