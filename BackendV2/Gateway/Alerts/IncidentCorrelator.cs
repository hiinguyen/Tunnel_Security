using BackendV2.Models;

namespace BackendV2.Gateway.Alerts;

/// <summary>Kết quả ghép: gắn vào tình huống có sẵn, tạo tình huống mới (kèm các cảnh báo cùng gắn), hoặc không làm gì.</summary>
public sealed record CorrelationDecision(Guid? AttachTo, IncidentType? CreateType, IReadOnlyList<Guid> Partners)
{
    public static readonly CorrelationDecision None = new(null, null, []);
}

/// <summary>
/// Quy tắc ghép cảnh báo cảm biến thành tình huống (docs mục 6.3), thuần để kiểm thử:
/// 1. Có tình huống đang mở cùng tuyến, cách tối đa 1 node, cập nhật trong 10 phút: gắn vào đó.
/// 2. Cảnh báo Critical: tạo tình huống mới.
/// 3. Cảnh báo High và có cảnh báo cảm biến khác chưa thuộc tình huống nào, cùng tuyến, cách tối đa 1 node,
///    tạo trong 60 giây: tạo tình huống gồm cả hai.
/// Cảnh báo thiết bị (mất kết nối) không tạo tình huống.
/// </summary>
public static class IncidentCorrelator
{
    public static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan IncidentActiveWindow = TimeSpan.FromMinutes(10);
    public const int MaxNodeDistance = 1;

    public static IncidentType TypeOf(string? sensorKind) => sensorKind switch
    {
        "radar" => IncidentType.Intrusion,
        "water" => IncidentType.Flood,
        null => IncidentType.DeviceFault,
        _ => IncidentType.Environment
    };

    public static CorrelationDecision Decide(
        Alert alert, IEnumerable<Alert> activeAlerts, IEnumerable<Incident> openIncidents, DateTimeOffset now)
    {
        if (alert.SensorId is null || alert.LineId is not { } line || alert.NodeByteId is not { } node || alert.IncidentId is not null)
        {
            return CorrelationDecision.None;
        }

        var nearby = openIncidents
            .Where(i => i.State != IncidentState.Closed
                        && i.LineId == line
                        && now - i.UpdatedAt <= IncidentActiveWindow
                        && ParseNodes(i.NodeByteIds).Any(n => Math.Abs(n - node) <= MaxNodeDistance))
            .OrderByDescending(i => i.UpdatedAt)
            .FirstOrDefault();
        if (nearby is not null)
        {
            return new CorrelationDecision(nearby.Id, null, []);
        }

        if (alert.Severity == AlertSeverity.Critical)
        {
            return new CorrelationDecision(null, TypeOf(alert.SensorKind), []);
        }

        if (alert.Severity != AlertSeverity.High)
        {
            return CorrelationDecision.None;
        }

        var partners = activeAlerts
            .Where(a => a.Id != alert.Id
                        && a.SensorId is not null
                        && a.IncidentId is null
                        && a.State != AlertState.Resolved
                        && a.LineId == line
                        && a.NodeByteId is { } n && Math.Abs(n - node) <= MaxNodeDistance
                        && now - a.CreatedAt <= PairWindow)
            .ToList();
        if (partners.Count == 0)
        {
            return CorrelationDecision.None;
        }

        var type = partners.Select(p => TypeOf(p.SensorKind)).Append(TypeOf(alert.SensorKind)).Max();
        return new CorrelationDecision(null, type, partners.Select(p => p.Id).ToList());
    }

    public static IReadOnlyList<int> ParseNodes(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    public static string FormatNodes(IEnumerable<int> nodes) => string.Join(',', nodes.Distinct().Order());
}
