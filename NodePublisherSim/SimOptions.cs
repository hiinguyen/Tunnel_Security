namespace NodePublisherSim;

/// <summary>Cấu hình trình giả lập, đọc từ biến môi trường để chạy được trong docker/CI mà không cần file.</summary>
public sealed record SimOptions(
    string BrokerHost,
    int BrokerPort,
    string? ProvisionUsername,
    string? ProvisionPassword,
    int Lines,
    int NodesPerLine,
    int FirstGatewayIndex,
    string GatewayIdPrefix,
    string DeviceSecret,
    TimeSpan PublishInterval,
    TimeSpan KeepAliveInterval,
    double EventProbability)
{
    /// <summary>Dải ID vật lý giả lập, khớp <c>DeviceKeyStore.SimulationRangeStart</c> ở BackendV2.</summary>
    public const uint PhysicalIdBase = 0x5100_0000;

    public static SimOptions FromEnvironment() => new(
        BrokerHost: Env("MQTT_BROKER_HOST") ?? "localhost",
        BrokerPort: int.Parse(Env("MQTT_BROKER_PORT") ?? "1883"),
        ProvisionUsername: Env("MQTT_PROVISION_USERNAME") ?? "provision",
        ProvisionPassword: Env("MQTT_PROVISION_PASSWORD"),
        Lines: int.Parse(Env("LINES") ?? "1"),
        NodesPerLine: int.Parse(Env("NODES_PER_LINE") ?? "4"),
        FirstGatewayIndex: int.Parse(Env("FIRST_GATEWAY_INDEX") ?? "1"),
        GatewayIdPrefix: Env("GATEWAY_ID_PREFIX") ?? "sim-gw",
        DeviceSecret: Env("SIM_DEVICE_SECRET") ?? "dev-sim-secret-change-me",
        PublishInterval: TimeSpan.FromSeconds(double.Parse(Env("PUBLISH_INTERVAL_SECONDS") ?? "1")),
        KeepAliveInterval: TimeSpan.FromSeconds(double.Parse(Env("KEEPALIVE_SECONDS") ?? "10")),
        EventProbability: double.Parse(Env("EVENT_PROBABILITY") ?? "0.002"));

    /// <summary>
    /// Số node của từng tuyến (theo thứ tự gateway 1, 2, ...) lấy từ nodes.json của bản đồ dashboard, để node giả lập
    /// chính là các hố ga trong file. Đặt LINES hoặc NODES_PER_LINE để quay lại topology tổng hợp LINES x NODES_PER_LINE.
    /// Thứ tự tuyến/node phải khớp BackendV2 MapNodeCatalog (tuyến theo lần xuất hiện đầu tiên, bỏ trạm hub).
    /// </summary>
    public IReadOnlyList<int> NodesPerGateway()
    {
        var names = NodeNamesPerGateway();
        return names.Count > 0 ? names.Select(n => n.Count).ToList() : Enumerable.Repeat(NodesPerLine, Lines).ToList();
    }

    /// <summary>Tên hố ga (trên bản đồ) của từng node, theo gateway; rỗng khi dùng topology tổng hợp.</summary>
    public IReadOnlyList<IReadOnlyList<string>> NodeNamesPerGateway() =>
        Env("LINES") is null && Env("NODES_PER_LINE") is null ? MapNodeNames() : [];

    private static List<IReadOnlyList<string>> MapNodeNames()
    {
        var path = Env("NODES_FILE") ?? FindNodesFile();
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("[NodePublisherSim] khong tim thay nodes.json, dung topology tong hop (LINES x NODES_PER_LINE)");
            return [];
        }

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var byLine = new List<(string Line, List<string> Names)>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            if (props.TryGetProperty("isHub", out var hub) && hub.GetBoolean())
            {
                continue;
            }

            var line = props.TryGetProperty("line", out var l) ? l.GetString() : null;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var name = props.TryGetProperty("name", out var n) ? n.GetString() : null;
            var group = byLine.FindIndex(g => g.Line == line);
            if (group < 0)
            {
                byLine.Add((line, [name ?? string.Empty]));
            }
            else
            {
                byLine[group].Names.Add(name ?? string.Empty);
            }
        }

        return byLine.Select(g => (IReadOnlyList<string>)g.Names).ToList();
    }

    private static string? FindNodesFile()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Station", "Assets", "Map", "nodes.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static uint PhysicalId(int gatewayIndex, int nodeIndex) => PhysicalIdBase + (uint)(gatewayIndex * 256 + nodeIndex);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
