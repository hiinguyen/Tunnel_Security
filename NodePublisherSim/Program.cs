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

var nodesPerGateway = options.NodesPerGateway();
var totalNodes = nodesPerGateway.Sum();
Console.WriteLine($"[NodePublisherSim] {nodesPerGateway.Count} line(s), {totalNodes} node(s) ({string.Join("+", nodesPerGateway)}) -> {options.BrokerHost}:{options.BrokerPort}");

var nodeNames = options.NodeNamesPerGateway();
var gateways = nodesPerGateway
    .Select((count, i) => new SimulatedGateway(options, options.FirstGatewayIndex + i, count, i < nodeNames.Count ? nodeNames[i] : null))
    .ToList();
using var control = SimControlServer.TryStart(gateways);
var runs = gateways.Select(g => g.RunAsync(cts.Token)).ToList();

var report = Task.Run(async () =>
{
    long last = 0;
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(_ => { });
        var total = gateways.Sum(g => g.Published);
        Console.WriteLine(
            $"[NodePublisherSim] joined {gateways.Sum(g => g.JoinedNodes)}/{totalNodes} nodes, " +
            $"{(total - last) / 10.0:0.#} msg/s");
        last = total;
    }
});

await Task.WhenAll(runs);
await report;
