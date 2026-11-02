using System.Text.Json;
using BackendV2.Gateway.Provisioning;

namespace BackendV2.Data;

/// <summary>Danh tính hiển thị của một hố ga trên bản đồ dashboard (Station/Assets/Map/nodes.json).</summary>
public sealed record MapNodeInfo(string Id, string Code, string Name, string LineName, double Latitude, double Longitude);

/// <summary>
/// Danh mục node của bản đồ dashboard, dùng để đặt tên/mã/vị trí cho node giả lập khi nó tham gia.
/// Quy ước thứ tự dùng chung với NodePublisherSim: tuyến theo thứ tự xuất hiện đầu tiên trong file
/// (bỏ trạm hub), node theo thứ tự trong file; gateway giả lập thứ g giữ tuyến thứ g và node giả lập thứ n
/// của nó là hố ga thứ n của tuyến. ID vật lý giả lập = SimulationRangeStart + g * 256 + n.
/// </summary>
public static class MapNodeCatalog
{
    private static readonly Lazy<IReadOnlyList<IReadOnlyList<MapNodeInfo>>> Lines = new(Load);

    /// <summary>Hố ga mà thiết bị giả lập <paramref name="physicalId"/> đóng vai; null nếu ngoài dải giả lập hoặc ngoài danh mục.</summary>
    public static MapNodeInfo? ForSimulatedDevice(uint physicalId)
    {
        if (physicalId is < DeviceKeyStore.SimulationRangeStart or > DeviceKeyStore.SimulationRangeEnd)
        {
            return null;
        }

        var offset = physicalId - DeviceKeyStore.SimulationRangeStart;
        int gateway = (int)(offset / 256), node = (int)(offset % 256);
        var lines = Lines.Value;
        return gateway >= 1 && gateway <= lines.Count && node >= 1 && node <= lines[gateway - 1].Count
            ? lines[gateway - 1][node - 1]
            : null;
    }

    private static IReadOnlyList<IReadOnlyList<MapNodeInfo>> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "SeedData", "nodes.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[MapNodeCatalog] Không tìm thấy {path}; node giả lập sẽ dùng tên mặc định");
            return [];
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var byLine = new List<(string Line, List<MapNodeInfo> Nodes)>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            if (props.TryGetProperty("isHub", out var hub) && hub.GetBoolean())
            {
                continue;
            }

            var id = props.GetProperty("id").GetString();
            var name = props.GetProperty("name").GetString();
            var line = props.TryGetProperty("line", out var l) ? l.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var coords = feature.GetProperty("geometry").GetProperty("coordinates");
            var code = props.TryGetProperty("code", out var c) ? c.GetString() : null;
            var info = new MapNodeInfo(id, string.IsNullOrWhiteSpace(code) ? id.ToUpperInvariant() : code, name, line,
                coords[1].GetDouble(), coords[0].GetDouble());

            var group = byLine.FindIndex(g => g.Line == line);
            if (group < 0)
            {
                byLine.Add((line, [info]));
            }
            else
            {
                byLine[group].Nodes.Add(info);
            }
        }

        return byLine.Select(g => (IReadOnlyList<MapNodeInfo>)g.Nodes).ToList();
    }
}
