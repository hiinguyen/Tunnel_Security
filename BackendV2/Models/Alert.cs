namespace BackendV2.Models;

public enum AlertSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum AlertState
{
    Open,
    Acknowledged,
    Resolved
}

/// <summary>Thứ tự khai báo là thứ tự ưu tiên khi một tình huống gộp nhiều loại cảnh báo.</summary>
public enum IncidentType
{
    DeviceFault,
    Environment,
    Flood,
    Intrusion
}

public enum IncidentState
{
    Open,
    Handling,
    Closed
}

/// <summary>
/// Cảnh báo (bản tin "Cảnh báo" của uplink). Mỗi <see cref="Key"/> chỉ có tối đa một cảnh báo chưa
/// Resolved, ví dụ <c>sensor:L1-N3-water-1</c>, <c>node-offline:L1-N3</c>, <c>gateway-offline:L1</c>.
/// </summary>
public class Alert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public AlertState State { get; set; } = AlertState.Open;

    public int? LineId { get; set; }
    public short? NodeByteId { get; set; }
    public string? NodeId { get; set; }
    public string? SensorId { get; set; }
    public string? SensorKind { get; set; }
    public short? SensorOrdinal { get; set; }
    public double? Value { get; set; }
    public double? Threshold { get; set; }
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    public Guid? IncidentId { get; set; }
}

/// <summary>Tình huống (bản tin "Tình huống" của uplink): nhóm các cảnh báo liên quan trên cùng một tuyến.</summary>
public class Incident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public IncidentType Type { get; set; }
    public AlertSeverity Severity { get; set; }
    public IncidentState State { get; set; } = IncidentState.Open;
    public int LineId { get; set; }

    /// <summary>Danh sách Node ID (byte) liên quan, ngăn cách bằng dấu phẩy, tăng dần.</summary>
    public string NodeByteIds { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? Operator { get; set; }
    public string? Note { get; set; }
}
