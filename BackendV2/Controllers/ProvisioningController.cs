using System.ComponentModel.DataAnnotations;
using BackendV2.Auth;
using BackendV2.Data;
using BackendV2.Gateway.Provisioning;
using BackendV2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WireProtocol;

namespace BackendV2.Controllers;

public sealed record ApproveGatewayRequest([StringLength(128)] string? Name);

public sealed record DeclareDeviceRequest(
    [Range(1, uint.MaxValue)] long PhysicalId,
    [Required, RegularExpression("^([0-9A-Fa-f]{2}){16,64}$", ErrorMessage = "Key must be 16..64 bytes in hex")] string KeyHex,
    [StringLength(128)] string? Label);

public sealed record DeviceIdentityView(long PhysicalId, string PhysicalIdHex, string? Label, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>Duyệt NM mới (docs mục 4.4) và khai báo thiết bị node được phép tham gia (mục 4.5).</summary>
[ApiController]
[Route("api/provisioning")]
[Authorize]
public class ProvisioningController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GatewayProvisioningService _gateways;
    private readonly NodeJoinService _join;
    private readonly DeviceKeyStore _keys;

    public ProvisioningController(AppDbContext db, GatewayProvisioningService gateways, NodeJoinService join, DeviceKeyStore keys)
    {
        _db = db;
        _gateways = gateways;
        _join = join;
        _keys = keys;
    }

    [HttpGet("gateways/pending")]
    public ActionResult<IReadOnlyList<PendingGateway>> PendingGateways() => Ok(_gateways.Pending);

    [HttpPost("gateways/{hardwareId}/approve")]
    [Authorize(Policy = Policies.SystemAdministration)]
    public async Task<ActionResult<Line>> Approve(string hardwareId, ApproveGatewayRequest request, CancellationToken ct)
    {
        if (!GatewayTopics.IsValidHardwareId(hardwareId))
        {
            return BadRequest(new { message = "Invalid hardware id" });
        }

        try
        {
            var line = await _gateways.ApproveAsync(hardwareId, request.Name, ct);
            _db.Audit(User, "gateway.approve", "Line", line.Id.ToString(), new { hardwareId, line.Name });
            await _db.SaveChangesAsync(ct);
            return Ok(line);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("gateways/{hardwareId}/reject")]
    [Authorize(Policy = Policies.SystemAdministration)]
    public async Task<IActionResult> Reject(string hardwareId, CancellationToken ct)
    {
        if (!GatewayTopics.IsValidHardwareId(hardwareId) || !await _gateways.RejectAsync(hardwareId, ct))
        {
            return NotFound();
        }

        _db.Audit(User, "gateway.reject", "Gateway", hardwareId);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Thiết bị chưa khai báo (hoặc đã thu hồi) vừa thử tham gia.</summary>
    [HttpGet("devices/pending")]
    public ActionResult<IReadOnlyList<PendingDevice>> PendingDevices() => Ok(_join.PendingDevices);

    [HttpGet("devices")]
    public async Task<ActionResult<List<DeviceIdentityView>>> Devices(CancellationToken ct) =>
        (await _db.DeviceIdentities.AsNoTracking().OrderBy(d => d.PhysicalId).ToListAsync(ct))
        .Select(View)
        .ToList();

    /// <summary>Khai báo thiết bị và khóa bí mật của nó. Khóa chỉ được ghi, không bao giờ đọc ra.</summary>
    [HttpPost("devices")]
    [Authorize(Policy = Policies.SystemAdministration)]
    public async Task<ActionResult<DeviceIdentityView>> Declare(DeclareDeviceRequest request, CancellationToken ct)
    {
        var existing = await _db.DeviceIdentities.FirstOrDefaultAsync(d => d.PhysicalId == request.PhysicalId, ct);
        if (existing is { RevokedAt: null })
        {
            return Conflict(new { message = "Device already declared; revoke it first to replace its key" });
        }

        var protectedKey = _keys.Protect(Convert.FromHexString(request.KeyHex));
        if (existing is null)
        {
            existing = new DeviceIdentity { PhysicalId = request.PhysicalId };
            _db.DeviceIdentities.Add(existing);
        }

        existing.Label = request.Label;
        existing.ProtectedKey = protectedKey;
        existing.CreatedAt = DateTimeOffset.UtcNow;
        existing.RevokedAt = null;
        _db.Audit(User, "device.declare", "DeviceIdentity", request.PhysicalId.ToString("X8"), new { request.Label });
        await _db.SaveChangesAsync(ct);

        _join.ForgetPendingDevice((uint)request.PhysicalId);
        return Ok(View(existing));
    }

    /// <summary>Thu hồi: ngừng nhận dữ liệu của node đang gắn với thiết bị ngay, và thiết bị không tham gia lại được.</summary>
    [HttpDelete("devices/{physicalId:long}")]
    [Authorize(Policy = Policies.SystemAdministration)]
    public async Task<IActionResult> Revoke(long physicalId, CancellationToken ct)
    {
        var device = await _db.DeviceIdentities.FirstOrDefaultAsync(d => d.PhysicalId == physicalId && d.RevokedAt == null, ct);
        if (device is null)
        {
            return NotFound();
        }

        device.RevokedAt = DateTimeOffset.UtcNow;
        _db.Audit(User, "device.revoke", "DeviceIdentity", physicalId.ToString("X8"));
        await _db.SaveChangesAsync(ct);

        // Thu hồi phải có hiệu lực ngay, không chờ node tự tham gia lại.
        await _join.DetachDeviceAsync((uint)physicalId, ct);
        return NoContent();
    }

    private static DeviceIdentityView View(DeviceIdentity d) =>
        new(d.PhysicalId, d.PhysicalId.ToString("X8"), d.Label, d.CreatedAt, d.RevokedAt);
}
