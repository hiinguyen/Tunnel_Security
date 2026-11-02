using BackendV2.Models;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Data;

/// <summary>
/// Không còn seed node: node chỉ xuất hiện khi thiết bị tham gia qua gateway (NodeJoinService), và node giả lập
/// của NodePublisherSim nhận tên/mã/vị trí từ <see cref="MapNodeCatalog"/>. Lớp này chỉ dọn các node hiển thị
/// do các bản seeder cũ tạo ra (node-1..node-20 và các hố ga seed theo bản đồ) để chúng không trùng với node thật.
/// </summary>
public static class TopologySeeder
{
    private const string MapSeedMarker = "chưa gắn thiết bị thật";

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var legacyDemoIds = Enumerable.Range(1, 20).Select(n => $"node-{n}").ToList();

        // Chỉ xóa node chưa từng gắn tuyến/thiết bị thật (LineId và PhysicalId đều trống).
        var stale = (await db.Nodes
                .Where(n => n.LineId == null && n.PhysicalId == null)
                .ToListAsync(ct))
            .Where(n => IsLegacyDemo(n, legacyDemoIds))
            .ToList();
        if (stale.Count == 0)
        {
            return;
        }

        var ids = stale.Select(n => n.Id).ToList();
        db.Sensors.RemoveRange(await db.Sensors.Where(s => ids.Contains(s.NodeId)).ToListAsync(ct));
        db.Cameras.RemoveRange(await db.Cameras.Where(c => ids.Contains(c.NodeId)).ToListAsync(ct));
        db.Nodes.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"[TopologySeeder] Đã xóa {stale.Count} node hiển thị cũ chưa gắn thiết bị thật");
    }

    private static bool IsLegacyDemo(Node n, List<string> legacyDemoIds)
    {
        var isMapSeed = n.NodeByteId is null && n.Description.EndsWith(MapSeedMarker, StringComparison.Ordinal);
        var isHubSeed = n.IsHub && n.NodeByteId is null && n.Description == "Trạm điều khiển trung tâm";
        var isOldDemo = legacyDemoIds.Contains(n.Id)
                        && (n.Name.StartsWith("Node giám sát hầm số", StringComparison.Ordinal)
                            || n.Name.StartsWith("Node giả lập", StringComparison.Ordinal)
                            || n.Name.StartsWith("Hố ga", StringComparison.Ordinal));
        return isMapSeed || isHubSeed || isOldDemo;
    }
}
