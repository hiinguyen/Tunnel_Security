namespace BackendV2.Gateway;

/// <summary>
/// Quy ước định danh dạng chuỗi trong CSDL. Node ID 1 byte chỉ duy nhất trong một tuyến,
/// nên mọi định danh đều chứa lineId (docs mục 7.1).
/// </summary>
public static class DeviceIds
{
    public static string Node(int lineId, byte nodeId) => $"L{lineId}-N{nodeId}";

    /// <summary>Camera gắn 1 - 1 với node (docs mục 5.4).</summary>
    public static string Camera(int lineId, byte nodeId) => $"CAM-L{lineId}-N{nodeId}";

    public static string Sensor(int lineId, byte nodeId, string kind, byte ordinal) => $"L{lineId}-N{nodeId}-{kind}-{ordinal}";

    public static string SensorAlertKey(string sensorId) => $"sensor:{sensorId}";

    public static string NodeOfflineAlertKey(int lineId, byte nodeId) => $"node-offline:{Node(lineId, nodeId)}";

    public static string GatewayOfflineAlertKey(int lineId) => $"gateway-offline:L{lineId}";
}
