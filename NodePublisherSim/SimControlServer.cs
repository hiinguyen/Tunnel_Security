using System.Net;
using System.Text.Json;

namespace NodePublisherSim;

/// <summary>
/// Cong dieu khien cuc bo (chi 127.0.0.1) de cong cu AlertInjector ep cam bien cua mot node vuot nguong.
/// Bat bang bien moi truong SIM_CONTROL_PORT (mac dinh 5199, dat 0 de tat).
/// </summary>
public sealed class SimControlServer : IDisposable
{
    public const int DefaultPort = 5199;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Gia tri tieu bieu cho tung muc, vuot nguong trong BackendV2 SensorFrameDecoder.Profiles.</summary>
    private static readonly Dictionary<string, (float Warning, float Critical)> Presets = new()
    {
        ["light"] = (780f, 850f),
        ["water"] = (1.8f, 2.8f),
        ["temp"] = (44f, 55f),
        ["hum"] = (96f, 99.5f),
        ["radar"] = (1f, 3f)
    };

    private readonly IReadOnlyList<SimulatedGateway> _gateways;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    private SimControlServer(IReadOnlyList<SimulatedGateway> gateways, int port)
    {
        _gateways = gateways;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public static SimControlServer? TryStart(IReadOnlyList<SimulatedGateway> gateways)
    {
        var raw = Environment.GetEnvironmentVariable("SIM_CONTROL_PORT");
        var port = int.Parse(string.IsNullOrEmpty(raw) ? DefaultPort.ToString() : raw);
        if (port <= 0)
        {
            return null;
        }

        try
        {
            var server = new SimControlServer(gateways, port);
            server._listener.Start();
            _ = Task.Run(server.LoopAsync);
            Console.WriteLine($"[NodePublisherSim] control endpoint http://127.0.0.1:{port}/ (AlertInjector)");
            return server;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NodePublisherSim] control endpoint disabled: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            try
            {
                await HandleAsync(context);
            }
            catch (Exception ex)
            {
                await WriteAsync(context, 400, new { error = ex.Message });
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath.TrimEnd('/');
        var method = context.Request.HttpMethod;

        if (method == "GET" && path == "/state")
        {
            await WriteAsync(context, 200, new { nodes = Snapshot() });
            return;
        }

        if (method != "POST" || path is not ("/inject" or "/clear"))
        {
            await WriteAsync(context, 404, new { error = "not found" });
            return;
        }

        var request = await JsonSerializer.DeserializeAsync<ControlRequest>(context.Request.InputStream, Json)
                      ?? throw new InvalidOperationException("empty body");

        if (path == "/clear" && request.Gateway is null)
        {
            foreach (var n in _gateways.SelectMany(g => g.Nodes))
            {
                n.ClearInjected(null);
            }

            await WriteAsync(context, 200, new { ok = true });
            return;
        }

        var node = _gateways.FirstOrDefault(g => g.Index == request.Gateway)?.Nodes.FirstOrDefault(n => n.Index == request.Node)
                   ?? throw new InvalidOperationException("node not found");

        if (path == "/clear")
        {
            node.ClearInjected(request.Sensor);
            await WriteAsync(context, 200, new { ok = true });
            return;
        }

        var sensor = request.Sensor ?? throw new InvalidOperationException("sensor is required");
        if (!Presets.TryGetValue(sensor, out var preset))
        {
            throw new InvalidOperationException($"unknown sensor '{sensor}'");
        }

        var value = request.Value ?? (request.Level == "critical" ? preset.Critical : preset.Warning);
        var until = request.DurationSeconds is > 0
            ? DateTimeOffset.UtcNow.AddSeconds(request.DurationSeconds.Value)
            : DateTimeOffset.MaxValue;
        node.Inject(sensor, value, until);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [control] gw{request.Gateway}/N{node.NodeId} {sensor}={value} for {request.DurationSeconds?.ToString() ?? "inf"}s");
        await WriteAsync(context, 200, new { ok = true, value });
    }

    private object Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return _gateways.SelectMany(g => g.Nodes.Select(n => new
        {
            gateway = g.Index,
            line = g.LineId,
            node = n.Index,
            nodeId = n.NodeId,
            name = n.Name,
            joined = n.NodeId is not null,
            overrides = n.ActiveOverrides(now).Select(o => new
            {
                sensor = o.Kind,
                value = o.Value,
                secondsLeft = o.Until == DateTimeOffset.MaxValue ? (double?)null : Math.Max(0, (o.Until - now).TotalSeconds)
            }).ToList()
        })).ToList();
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.OutputStream, body, Json);
        context.Response.Close();
    }

    private sealed record ControlRequest(int? Gateway, int Node, string? Sensor, string? Level, float? Value, int? DurationSeconds);
}
