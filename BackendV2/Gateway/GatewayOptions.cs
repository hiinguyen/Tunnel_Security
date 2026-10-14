namespace BackendV2.Gateway;

/// <summary>Kết nối của BackendV2 tới MQTT broker trên PC (section "Mqtt").</summary>
public sealed class MqttOptions
{
    public const string Section = "Mqtt";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; }
    public string ClientId { get; set; } = "backendv2";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int KeepAliveSeconds { get; set; } = 15;
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>Số bản tin nhận được chờ xử lý tối đa; khi đầy thì bỏ bản cũ nhất (docs mục 8.2).</summary>
    public int InboxCapacity { get; set; } = 100_000;

    /// <summary>
    /// Khi có giá trị, uplink dữ liệu A7 được nhận qua shared subscription <c>$share/{group}/...</c>
    /// để nhiều instance BackendV2 chia tải (docs mục 8.2).
    /// </summary>
    public string? SharedSubscriptionGroup { get; set; }

    public DynamicSecurityOptions DynamicSecurity { get; set; } = new();
}

/// <summary>Plugin Dynamic Security của Mosquitto: tạo tài khoản và ACL cho NM lúc đang chạy (docs mục 4.4).</summary>
public sealed class DynamicSecurityOptions
{
    public bool Enabled { get; set; }

    /// <summary>Tài khoản dùng chung để NM mới gửi yêu cầu cấp tuyến, chỉ được dùng topic <c>ts/prov/{clientId}/...</c>.</summary>
    public string ProvisioningUsername { get; set; } = "provision";

    public string? ProvisioningPassword { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 5;
}

/// <summary>Tham số giao thức PC - NM (section "Gateway").</summary>
public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    /// <summary>Node coi là offline khi không có uplink trong khoảng này (3 chu kỳ A4 10 giây).</summary>
    public int NodeTimeoutSeconds { get; set; } = 30;

    public int CommandTimeoutSeconds { get; set; } = 3;
    public int CommandMaxAttempts { get; set; } = 3;
    public int JoinChallengeTtlSeconds { get; set; } = 30;
    public int PendingRequestTtlSeconds { get; set; } = 300;

    /// <summary>Chỉ nên bật khi phát triển: NM mới được cấp tuyến ngay, không cần operator duyệt.</summary>
    public bool AutoApproveGateways { get; set; }

    /// <summary>Chu kỳ lấy mẫu mặc định gửi cho node trong bản tin cấp ID (A3).</summary>
    public uint DefaultSampleIntervalMs { get; set; } = 1000;

    /// <summary>Số bản đo trở về mức thấp hơn liên tiếp trước khi hạ mức cảnh báo, để tránh bật tắt liên tục.</summary>
    public int LevelRecoverySamples { get; set; } = 3;
}

/// <summary>Định danh trạm và chu kỳ bản tin "An toàn" gửi lên Center (section "Station").</summary>
public sealed class StationOptions
{
    public const string Section = "Station";

    public string Id { get; set; } = "ST-01";
    public int SafetyIntervalSeconds { get; set; } = 30;
}

/// <summary>Ghi chuỗi thời gian theo lô (section "ReadingWriter", docs mục 8.2).</summary>
public sealed class ReadingWriterOptions
{
    public const string Section = "ReadingWriter";

    public int Capacity { get; set; } = 50_000;
    public int BatchSize { get; set; } = 1_000;
    public int FlushIntervalMs { get; set; } = 500;
}

/// <summary>
/// Chỉ dùng khi phát triển với NodePublisherSim: thiết bị có ID vật lý trong dải giả lập được tự khai báo
/// với khóa = HMAC-SHA256(DeviceSecret, ID vật lý). Để trống ở môi trường thật.
/// </summary>
public sealed class SimulationOptions
{
    public const string Section = "Simulation";

    public string? DeviceSecret { get; set; }
}
