namespace BackendV2.Models;

/// <summary>
/// Thiết bị node đã khai báo: ID vật lý (4 byte, nạp khi sản xuất) và khóa bí mật dùng cho
/// xác thực thách đố A1. Khóa được lưu ở dạng đã mã hóa bằng ASP.NET Data Protection, không bao giờ trả ra API.
/// </summary>
public class DeviceIdentity
{
    /// <summary>ID vật lý uint32, lưu dạng long vì SQL Server không có kiểu số nguyên không dấu.</summary>
    public long PhysicalId { get; set; }

    public string? Label { get; set; }
    public string ProtectedKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}
