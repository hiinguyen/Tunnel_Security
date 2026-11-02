using WireProtocol;

namespace WireProtocol.Tests;

public class GatewayTopicsTests
{
    [Theory]
    [InlineData(UplinkKind.Data, "ts/3/n/7/up/data")]
    [InlineData(UplinkKind.Status, "ts/3/n/7/up/status")]
    [InlineData(UplinkKind.Config, "ts/3/n/7/up/config")]
    [InlineData(UplinkKind.Ack, "ts/3/n/7/up/ack")]
    [InlineData(UplinkKind.Join, "ts/3/n/7/up/join")]
    public void NodeUplink_builds_topic_that_parses_back(UplinkKind kind, string expected)
    {
        var topic = GatewayTopics.NodeUplink(3, 7, kind);

        Assert.Equal(expected, topic);
        Assert.True(GatewayTopics.TryParse(topic, out var address));
        Assert.Equal(new TopicAddress(TopicKind.NodeUplink, 3, 7, kind, null), address);
    }

    [Fact]
    public void Join_topic_uses_node_zero()
    {
        Assert.Equal("ts/12/n/0/up/join", GatewayTopics.Join(12));
    }

    [Fact]
    public void Gateway_and_downlink_topics_round_trip()
    {
        Assert.True(GatewayTopics.TryParse(GatewayTopics.GatewayState(254), out var state));
        Assert.Equal(TopicKind.GatewayState, state.Kind);
        Assert.Equal(254, state.LineId);

        Assert.True(GatewayTopics.TryParse(GatewayTopics.GatewayStatus(1), out var status));
        Assert.Equal(TopicKind.GatewayStatus, status.Kind);

        Assert.True(GatewayTopics.TryParse(GatewayTopics.NodeDownlink(5, 255), out var down));
        Assert.Equal(new TopicAddress(TopicKind.NodeDownlink, 5, 255, null, null), down);
    }

    [Fact]
    public void Provision_topics_carry_hardware_id()
    {
        Assert.Equal("ts/prov/pi-AB12/req", GatewayTopics.ProvisionRequest("pi-AB12"));
        Assert.True(GatewayTopics.TryParse("ts/prov/pi-AB12/req", out var req));
        Assert.Equal(TopicKind.ProvisionRequest, req.Kind);
        Assert.Equal("pi-AB12", req.HardwareId);

        Assert.True(GatewayTopics.TryParse(GatewayTopics.ProvisionResponse("pi-AB12"), out var resp));
        Assert.Equal(TopicKind.ProvisionResponse, resp.Kind);
    }

    [Theory]
    [InlineData("sensors/1/reading")]
    [InlineData("ts/0/gw/state")]
    [InlineData("ts/255/gw/state")]
    [InlineData("ts/abc/gw/state")]
    [InlineData("ts/1/n/256/up/data")]
    [InlineData("ts/1/n/3/up/unknown")]
    [InlineData("ts/1/n/3/up")]
    [InlineData("ts/1/gw/other")]
    [InlineData("ts/prov/bad id/req")]
    [InlineData("ts/1/n/3/up/data/extra")]
    public void Rejects_malformed_topics(string topic)
    {
        Assert.False(GatewayTopics.TryParse(topic, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Builders_reject_out_of_range_line(int lineId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GatewayTopics.GatewayState(lineId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a+")]
    [InlineData("a#")]
    public void Builders_reject_invalid_hardware_id(string hardwareId)
    {
        Assert.Throws<ArgumentException>(() => GatewayTopics.ProvisionRequest(hardwareId));
    }
}
