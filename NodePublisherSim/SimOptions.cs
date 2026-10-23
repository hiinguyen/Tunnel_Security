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

    public static uint PhysicalId(int gatewayIndex, int nodeIndex) => PhysicalIdBase + (uint)(gatewayIndex * 256 + nodeIndex);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
