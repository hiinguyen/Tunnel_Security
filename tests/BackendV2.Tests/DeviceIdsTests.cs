using BackendV2.Gateway;

namespace BackendV2.Tests;

public class DeviceIdsTests
{
    [Fact]
    public void Camera_id_follows_line_node_convention() =>
        Assert.Equal("CAM-L3-N7", DeviceIds.Camera(3, 7));
}
