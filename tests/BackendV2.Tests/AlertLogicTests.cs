using BackendV2.Gateway;
using BackendV2.Gateway.Alerts;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Uplink;
using BackendV2.Models;

namespace BackendV2.Tests;

public class RuleEngineTests
{
    [Theory]
    [InlineData(1.0, ReadingLevel.Normal)]
    [InlineData(1.5, ReadingLevel.Warning)]
    [InlineData(2.4, ReadingLevel.Warning)]
    [InlineData(2.5, ReadingLevel.Critical)]
    public void Classifies_against_thresholds_inclusively(double value, ReadingLevel expected)
    {
        Assert.Equal(expected, RuleEngine.Classify(value, 1.5, 2.5));
    }

    [Fact]
    public void Missing_thresholds_never_alert()
    {
        Assert.Equal(ReadingLevel.Normal, RuleEngine.Classify(1e9, null, null));
    }
}

public class SensorLevelTrackerTests
{
    [Fact]
    public void Escalates_immediately_and_recovers_after_consecutive_lower_samples()
    {
        var tracker = new SensorLevelTracker(recoverySamples: 3);

        Assert.Null(tracker.Observe("s", ReadingLevel.Normal));
        Assert.Equal(ReadingLevel.Critical, tracker.Observe("s", ReadingLevel.Critical));
        Assert.Null(tracker.Observe("s", ReadingLevel.Normal));
        Assert.Null(tracker.Observe("s", ReadingLevel.Normal));
        Assert.Equal(ReadingLevel.Normal, tracker.Observe("s", ReadingLevel.Normal));
    }

    [Fact]
    public void A_flapping_sensor_does_not_produce_repeated_changes()
    {
        var tracker = new SensorLevelTracker(recoverySamples: 3);
        tracker.Observe("s", ReadingLevel.Warning);

        var changes = Enumerable.Range(0, 20)
            .Select(i => tracker.Observe("s", i % 2 == 0 ? ReadingLevel.Normal : ReadingLevel.Warning))
            .Where(c => c is not null)
            .ToList();

        Assert.Empty(changes);
    }

    [Fact]
    public void Recovery_lands_on_the_highest_level_seen_during_the_streak()
    {
        var tracker = new SensorLevelTracker(recoverySamples: 3);
        tracker.Observe("s", ReadingLevel.Critical);

        tracker.Observe("s", ReadingLevel.Normal);
        tracker.Observe("s", ReadingLevel.Warning);

        Assert.Equal(ReadingLevel.Warning, tracker.Observe("s", ReadingLevel.Normal));
    }

    [Fact]
    public void Reset_makes_the_next_abnormal_reading_report_again()
    {
        var tracker = new SensorLevelTracker(recoverySamples: 3);
        tracker.Observe("s", ReadingLevel.Critical);
        Assert.Null(tracker.Observe("s", ReadingLevel.Critical));

        tracker.Reset("s");

        Assert.Equal(ReadingLevel.Critical, tracker.Observe("s", ReadingLevel.Critical));
    }

    [Fact]
    public void Sensors_are_independent()
    {
        var tracker = new SensorLevelTracker(recoverySamples: 2);

        Assert.Equal(ReadingLevel.Warning, tracker.Observe("a", ReadingLevel.Warning));
        Assert.Null(tracker.Observe("b", ReadingLevel.Normal));
    }
}

public class IncidentCorrelatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static Alert SensorAlert(int line, short node, string kind, AlertSeverity severity, DateTimeOffset? at = null) => new()
    {
        Key = $"sensor:L{line}-N{node}-{kind}-1",
        LineId = line,
        NodeByteId = node,
        SensorId = $"L{line}-N{node}-{kind}-1",
        SensorKind = kind,
        Severity = severity,
        CreatedAt = at ?? Now,
        UpdatedAt = at ?? Now
    };

    [Fact]
    public void Critical_alert_creates_an_incident_of_its_type()
    {
        var alert = SensorAlert(1, 3, "water", AlertSeverity.Critical);

        var decision = IncidentCorrelator.Decide(alert, [alert], [], Now);

        Assert.Equal(IncidentType.Flood, decision.CreateType);
        Assert.Null(decision.AttachTo);
    }

    [Fact]
    public void Lone_high_alert_does_not_create_an_incident()
    {
        var alert = SensorAlert(1, 3, "temp", AlertSeverity.High);

        Assert.Equal(CorrelationDecision.None, IncidentCorrelator.Decide(alert, [alert], [], Now));
    }

    [Fact]
    public void Two_high_alerts_on_adjacent_nodes_within_a_minute_form_an_incident_with_the_dominant_type()
    {
        var earlier = SensorAlert(1, 4, "temp", AlertSeverity.High, Now.AddSeconds(-30));
        var alert = SensorAlert(1, 3, "radar", AlertSeverity.High);

        var decision = IncidentCorrelator.Decide(alert, [earlier, alert], [], Now);

        Assert.Equal(IncidentType.Intrusion, decision.CreateType);
        Assert.Equal([earlier.Id], decision.Partners);
    }

    [Theory]
    [InlineData(2, 4, 0)]   // khác tuyến
    [InlineData(1, 6, 0)]   // cách 3 node
    [InlineData(1, 4, -90)] // quá 60 giây
    public void Distant_or_old_alerts_are_not_paired(int line, short node, int secondsAgo)
    {
        var other = SensorAlert(line, node, "temp", AlertSeverity.High, Now.AddSeconds(secondsAgo));
        var alert = SensorAlert(1, 3, "temp", AlertSeverity.High);

        Assert.Equal(CorrelationDecision.None, IncidentCorrelator.Decide(alert, [other, alert], [], Now));
    }

    [Fact]
    public void Alert_near_an_open_incident_attaches_to_it()
    {
        var incident = new Incident { LineId = 1, NodeByteIds = "3,4", UpdatedAt = Now.AddMinutes(-2) };
        var alert = SensorAlert(1, 5, "light", AlertSeverity.High);

        var decision = IncidentCorrelator.Decide(alert, [alert], [incident], Now);

        Assert.Equal(incident.Id, decision.AttachTo);
    }

    [Fact]
    public void Stale_incident_is_not_reused()
    {
        var incident = new Incident { LineId = 1, NodeByteIds = "3", UpdatedAt = Now.AddMinutes(-11) };
        var alert = SensorAlert(1, 3, "water", AlertSeverity.Critical);

        var decision = IncidentCorrelator.Decide(alert, [alert], [incident], Now);

        Assert.Null(decision.AttachTo);
        Assert.Equal(IncidentType.Flood, decision.CreateType);
    }

    [Fact]
    public void Device_alerts_are_never_correlated()
    {
        var alert = new Alert { LineId = 1, NodeByteId = 3, Severity = AlertSeverity.Critical, CreatedAt = Now };

        Assert.Equal(CorrelationDecision.None, IncidentCorrelator.Decide(alert, [alert], [], Now));
    }
}

public class SafetyCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly LineEntry[] Lines = [new(1, "Tuyến 1", "gw1"), new(2, "Tuyến 2", "gw2"), new(3, "Tuyến 3", "gw3")];

    [Fact]
    public void Each_line_gets_its_own_level_and_the_station_takes_the_worst()
    {
        GatewayHealthSnapshot[] gateways =
        [
            new(1, Liveness.Online, Now, null),
            new(2, Liveness.Online, Now, null),
            new(3, Liveness.Offline, Now, null)
        ];
        NodeHealthSnapshot[] nodes =
        [
            new(1, 1, Liveness.Online, Now, null),
            new(1, 2, Liveness.Online, Now, null),
            new(2, 1, Liveness.Online, Now, null),
            new(2, 2, Liveness.Offline, Now, null),
            new(3, 1, Liveness.Unknown, Now, null)
        ];
        var alerts = new AlertSnapshot([new ActiveAlertInfo(Guid.NewGuid(), 2, AlertSeverity.Medium)], []);

        var status = SafetyCalculator.Compute("ST-01", Now, Lines, gateways, nodes, alerts);

        Assert.Equal(["safe", "warning", "unknown"], status.Lines.Select(l => l.Level));
        Assert.Equal("warning", status.Level);
        Assert.Equal((2, 2), (status.Lines[0].NodesOnline, status.Lines[0].NodesTotal));
        Assert.Equal((1, 2), (status.Lines[1].NodesOnline, status.Lines[1].NodesTotal));
        Assert.Equal(1, status.OpenAlerts);
    }

    [Fact]
    public void Critical_alert_makes_the_line_and_station_critical()
    {
        var alerts = new AlertSnapshot([new ActiveAlertInfo(Guid.NewGuid(), 1, AlertSeverity.Critical)], []);

        var status = SafetyCalculator.Compute("ST-01", Now, Lines.Take(1), [new(1, Liveness.Online, Now, null)], [], alerts);

        Assert.Equal("critical", status.Level);
    }

    [Fact]
    public void Station_without_lines_is_unknown()
    {
        Assert.Equal("unknown", SafetyCalculator.Compute("ST-01", Now, [], [], [], AlertSnapshot.Empty).Level);
    }
}
