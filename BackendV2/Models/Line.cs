namespace BackendV2.Models;

/// <summary>Trạng thái sống/chết của NM hoặc node, theo docs/lan-architecture-protocol.md mục 4.7.</summary>
public enum Liveness
{
    Unknown,
    Online,
    Offline
}

/// <summary>
/// Tuyến = một NM (Gateway tuyến) và các node phía sau. <see cref="Id"/> là lineId trong topic
/// <c>ts/{lineId}/...</c> (1..254), do PC cấp khi duyệt NM và không tái sử dụng sau khi gỡ tuyến,
/// để dữ liệu lịch sử của tuyến cũ không bị lẫn với tuyến mới.
/// </summary>
public class Line
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GatewayHardwareId { get; set; } = string.Empty;
    public string? GatewayIp { get; set; }
    public string? GatewayMac { get; set; }
    public string? GatewayVersion { get; set; }
    public Liveness Status { get; set; } = Liveness.Unknown;
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetiredAt { get; set; }
}
