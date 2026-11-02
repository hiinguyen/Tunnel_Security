using Microsoft.AspNetCore.Authorization;

namespace BackendV2.Auth;

/// <summary>
/// Policy phân quyền theo nhóm chức năng (claim "permission" trong JWT, xem AuthSeeder).
/// API chỉ đọc chỉ cần đăng nhập; API thay đổi thiết bị, cảnh báo hay cấp phát cần đúng nhóm chức năng.
/// </summary>
public static class Policies
{
    public const string DeviceManagement = "DEVICE_MANAGEMENT";
    public const string AlertManagement = "ALERT_EVENT_MANAGEMENT";
    public const string SystemAdministration = "SYSTEM_ADMINISTRATION";

    private const string PermissionClaim = "permission";

    public static void AddGatewayPolicies(this AuthorizationOptions options)
    {
        foreach (var permission in new[] { DeviceManagement, AlertManagement, SystemAdministration })
        {
            options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim(PermissionClaim, permission));
        }
    }
}
