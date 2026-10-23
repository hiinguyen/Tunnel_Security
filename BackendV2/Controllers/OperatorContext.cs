using System.Security.Claims;
using System.Text.Json;
using BackendV2.Data;
using BackendV2.Models;

namespace BackendV2.Controllers;

/// <summary>Người thao tác hiện tại và ghi nhật ký kiểm toán cho các thao tác ảnh hưởng thiết bị.</summary>
internal static class OperatorContext
{
    public static string Actor(this ClaimsPrincipal user) =>
        string.IsNullOrEmpty(user.Identity?.Name) ? "unknown" : user.Identity!.Name!;

    public static void Audit(this AppDbContext db, ClaimsPrincipal user, string action, string targetType, string targetId, object? value = null)
    {
        Guid? actorId = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            NewValueJson = value is null ? null : JsonSerializer.Serialize(value)
        });
    }
}
