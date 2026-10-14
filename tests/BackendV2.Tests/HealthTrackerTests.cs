using BackendV2.Gateway.Health;
using BackendV2.Models;
using WireProtocol;

namespace BackendV2.Tests;

public class HealthTrackerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Node_goes_online_when_seen_and_offline_after_timeout()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.GatewayState(1, online: true, T0);

        var online = tracker.NodeSeen(1, 3, T0, null);
        Assert.Equal([new HealthTransition(1, 3, Liveness.Unknown, Liveness.Online, T0)], online);

        Assert.Empty(tracker.Tick(T0 + Timeout - TimeSpan.FromSeconds(1)));
        var offline = tracker.Tick(T0 + Timeout);
        Assert.Equal([new HealthTransition(1, 3, Liveness.Online, Liveness.Offline, T0 + Timeout)], offline);
    }

    [Fact]
    public void Repeated_traffic_postpones_the_deadline_and_emits_no_transitions()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.GatewayState(1, online: true, T0);
        tracker.NodeSeen(1, 3, T0, null);

        for (var s = 10; s <= 100; s += 10)
        {
            Assert.Empty(tracker.NodeSeen(1, 3, T0.AddSeconds(s), null));
            Assert.Empty(tracker.Tick(T0.AddSeconds(s)));
        }

        Assert.Single(tracker.Tick(T0.AddSeconds(130)));
    }

    [Fact]
    public void Gateway_offline_marks_nodes_unknown_and_stops_their_deadlines()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.GatewayState(1, online: true, T0);
        tracker.NodeSeen(1, 1, T0, null);
        tracker.NodeSeen(1, 2, T0, null);
        tracker.NodeSeen(2, 1, T0, null);

        var transitions = tracker.GatewayState(1, online: false, T0.AddSeconds(5));

        Assert.Equal(3, transitions.Count);
        Assert.True(transitions[0].IsGateway);
        Assert.All(transitions.Skip(1), t => Assert.Equal(Liveness.Unknown, t.To));
        Assert.Equal(Liveness.Online, tracker.Nodes().Single(n => n.LineId == 2).State);

        // Hết hạn trong khi NM mất kết nối: không chuyển sang Offline vì không có cơ sở để biết.
        Assert.DoesNotContain(tracker.Tick(T0.AddMinutes(5)), t => t.LineId == 1);
    }

    [Fact]
    public void Gateway_back_online_gives_unknown_nodes_a_fresh_deadline()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.GatewayState(1, online: true, T0);
        tracker.NodeSeen(1, 1, T0, null);
        tracker.GatewayState(1, online: false, T0.AddSeconds(1));

        var back = T0.AddMinutes(10);
        tracker.GatewayState(1, online: true, back);

        Assert.Empty(tracker.Tick(back + Timeout - TimeSpan.FromSeconds(1)));
        var transitions = tracker.Tick(back + Timeout);
        Assert.Equal(Liveness.Unknown, Assert.Single(transitions).From);
        Assert.Equal(Liveness.Offline, transitions[0].To);
    }

    [Fact]
    public void Registered_node_never_heard_goes_offline_after_timeout()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.AddNode(4, 9, T0);

        var transitions = tracker.Tick(T0 + Timeout);

        Assert.Equal(new HealthTransition(4, 9, Liveness.Unknown, Liveness.Offline, T0 + Timeout), Assert.Single(transitions));
    }

    [Fact]
    public void Keep_alive_status_is_kept_in_snapshot()
    {
        var tracker = new HealthTracker(Timeout);
        var status = new KeepAliveStatus(55f, NodePowerState.Busy, NodeErrorFlags.SensorFault, []);

        tracker.NodeSeen(1, 1, T0, status);
        tracker.NodeSeen(1, 1, T0.AddSeconds(1), null);

        Assert.Equal(status, tracker.Nodes().Single().LastStatus);
    }

    [Fact]
    public void Removed_node_never_times_out()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.NodeSeen(1, 1, T0, null);
        tracker.NodeSeen(1, 2, T0, null);

        tracker.RemoveNode(1, 1);

        Assert.Equal([(byte)2], tracker.Nodes().Select(n => n.NodeByteId));
        Assert.All(tracker.Tick(T0.AddHours(1)), t => Assert.Equal((byte)2, t.NodeByteId));
    }

    [Fact]
    public void Reset_deadlines_gives_nodes_a_fresh_timeout_after_the_pc_reconnects()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.GatewayState(1, online: true, T0);
        tracker.NodeSeen(1, 1, T0, null);

        // PC mất kết nối broker 5 phút: không có Tick trong lúc đó, rồi đặt lại hạn khi kết nối lại.
        var back = T0.AddMinutes(5);
        tracker.ResetDeadlines(back);

        Assert.Empty(tracker.Tick(back));
        Assert.Empty(tracker.Tick(back + Timeout - TimeSpan.FromSeconds(1)));
        Assert.Single(tracker.Tick(back + Timeout));
    }

    [Fact]
    public void Removing_a_line_drops_its_gateway_and_nodes()
    {
        var tracker = new HealthTracker(Timeout);
        tracker.NodeSeen(1, 1, T0, null);
        tracker.NodeSeen(2, 1, T0, null);

        tracker.RemoveLine(1);

        Assert.Equal([2], tracker.Gateways().Select(g => g.LineId));
        Assert.Equal([2], tracker.Nodes().Select(n => n.LineId));
        Assert.All(tracker.Tick(T0.AddHours(1)), t => Assert.Equal(2, t.LineId));
    }
}
