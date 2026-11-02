using BackendV2.Gateway.Alerts;
using BackendV2.Gateway.Health;
using BackendV2.Models;

namespace BackendV2.Gateway.Uplink;

public sealed record LineSafety(int LineId, string Gateway, int NodesOnline, int NodesTotal, string Level);

/// <summary>Bản tin "An toàn" (docs mục 6.1): chỉ tổng hợp theo tuyến, kích thước tăng theo số tuyến chứ không theo số node.</summary>
public sealed record SafetyStatus(
    string StationId, DateTimeOffset Ts, string Level, IReadOnlyList<LineSafety> Lines, int OpenAlerts, int OpenIncidents);

public static class SafetyCalculator
{
    public const string Safe = "safe";
    public const string Unknown = "unknown";
    public const string Warning = "warning";
    public const string Critical = "critical";

    // Thứ tự mức nặng dần, dùng để lấy mức nặng nhất.
    private static readonly string[] Order = [Safe, Unknown, Warning, Critical];

    public static SafetyStatus Compute(
        string stationId,
        DateTimeOffset now,
        IEnumerable<LineEntry> lines,
        IReadOnlyList<GatewayHealthSnapshot> gateways,
        IReadOnlyList<NodeHealthSnapshot> nodes,
        AlertSnapshot alerts)
    {
        var gatewayByLine = gateways.ToDictionary(g => g.LineId);
        var nodesByLine = nodes.ToLookup(n => n.LineId);
        var alertsByLine = alerts.Alerts.Where(a => a.LineId is not null).ToLookup(a => a.LineId!.Value);
        var incidentsByLine = alerts.Incidents.ToLookup(i => i.LineId);

        var lineStatuses = lines.OrderBy(l => l.Id).Select(line =>
        {
            var gateway = gatewayByLine.TryGetValue(line.Id, out var g) ? g.State : Liveness.Unknown;
            var lineNodes = nodesByLine[line.Id].ToList();
            var online = lineNodes.Count(n => n.State == Liveness.Online);
            var worstAlert = alertsByLine[line.Id].Select(a => a.Severity)
                .Concat(incidentsByLine[line.Id].Select(i => i.Severity))
                .DefaultIfEmpty(AlertSeverity.Low)
                .Max();
            var hasAlerts = alertsByLine[line.Id].Any() || incidentsByLine[line.Id].Any();

            var level = worstAlert == AlertSeverity.Critical ? Critical
                : hasAlerts || lineNodes.Any(n => n.State == Liveness.Offline) ? Warning
                : gateway != Liveness.Online ? Unknown
                : Safe;

            return new LineSafety(line.Id, gateway.ToString().ToLowerInvariant(), online, lineNodes.Count, level);
        }).ToList();

        var overall = lineStatuses.Count == 0 ? Unknown : lineStatuses.Select(l => l.Level).MaxBy(Rank)!;
        return new SafetyStatus(stationId, now, overall, lineStatuses, alerts.Alerts.Count, alerts.Incidents.Count);
    }

    private static int Rank(string level) => Array.IndexOf(Order, level);
}
