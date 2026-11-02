namespace LineGateway;

public sealed class LineGatewayOptions
{
    public const string Section = "LineGateway";

    /// <summary>ID phần cứng của NM, làm client id MQTT khi xin cấp tuyến (lan doc 4.4). Trống = lấy /etc/machine-id.</summary>
    public string HardwareId { get; set; } = string.Empty;

    /// <summary>Nơi lưu tuyến và tài khoản MQTT đã được cấp, để khởi động lại không phải xin duyệt lại.</summary>
    public string StatePath { get; set; } = "/var/lib/line-gateway/state.json";

    public string MqttHost { get; set; } = "10.0.0.10";
    public int MqttPort { get; set; } = 8883;
    public bool MqttUseTls { get; set; } = true;
    public string ProvisionUsername { get; set; } = "provision";
    public string? ProvisionPassword { get; set; }

    public int GsanPort { get; set; } = 9300;
    public int VideoPort { get; set; } = 6000;
    public int MetaPort { get; set; } = 6002;

    /// <summary>Gốc WS ingest camera trên PC; NM nối tới {gốc}/CAM-L{k}-N{n}/ingest.</summary>
    public string CameraIngestBaseUrl { get; set; } = "ws://10.0.0.10:5080/ws/camera";

    /// <summary>Node không gửi gì quá thời gian này thì đóng kết nối (heartbeat SDD là 5 s).</summary>
    public int NodeIdleSeconds { get; set; } = 30;

    public int MaxConnections { get; set; } = 512;
}
