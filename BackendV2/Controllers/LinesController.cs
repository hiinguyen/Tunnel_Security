using System.ComponentModel.DataAnnotations;
using BackendV2.Auth;
using BackendV2.Data;
using BackendV2.Gateway.Health;
using BackendV2.Gateway.Provisioning;
using BackendV2.Gateway.Uplink;
using BackendV2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Controllers;

public sealed record LineView(
    int Id, string Name, string GatewayHardwareId, string? GatewayIp, string? GatewayVersion,
    Liveness Gateway, DateTimeOffset? LastSeenAt, int NodesOnline, int NodesTotal, DateTimeOffset? RetiredAt);

public sealed record LineNodeView(
    string Id, byte NodeByteId, string Name, Liveness State, DateTimeOffset? LastSeen,
    float? BatteryPercent, string? PowerState, string? Errors, long? PhysicalId);

public sealed record RenameLineRequest([Required, StringLength(128, MinimumLength = 1)] string Name);

/// <summary>Tuyến (NM) và node của tuyến, kèm trạng thái sống/chết hiện tại trong bộ nhớ.</summary>
[ApiController]
[Route("api/lines")]
[Authorize]
public class LinesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DeviceHealthService _health;
    private readonly GatewayProvisioningService _provisioning;
    private readonly UplinkPublisher _uplink;

    public LinesController(AppDbContext db, DeviceHealthService health, GatewayProvisioningService provisioning, UplinkPublisher uplink)
    {
        _db = db;
        _health = health;
        _provisioning = provisioning;
        _uplink = uplink;
    }

    [HttpGet]
    public async Task<ActionResult<List<LineView>>> GetAll([FromQuery] bool includeRetired = false, CancellationToken ct = default)
    {
        var lines = await _db.Lines.AsNoTracking()
            .Where(l => includeRetired || l.RetiredAt == null)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);
        var gateways = _health.Gateways().ToDictionary(g => g.LineId);
        var nodes = _health.Nodes().ToLookup(n => n.LineId);

        return lines.Select(l => new LineView(
            l.Id, l.Name, l.GatewayHardwareId, l.GatewayIp, l.GatewayVersion,
            gateways.TryGetValue(l.Id, out var g) ? g.State : l.Status,
            gateways.TryGetValue(l.Id, out var g2) && g2.LastSeen is not null ? g2.LastSeen : l.LastSeenAt,
            nodes[l.Id].Count(n => n.State == Liveness.Online),
            nodes[l.Id].Count(),
            l.RetiredAt)).ToList();
    }

    /// <summary>Trạng thái "An toàn" gần nhất, giống bản tin gửi lên Center.</summary>
    [HttpGet("safety")]
    public ActionResult<SafetyStatus> GetSafety() =>
        _uplink.LastSafety is { } status ? Ok(status) : NoContent();

    [HttpGet("{lineId:int}/nodes")]
    public async Task<ActionResult<List<LineNodeView>>> GetNodes(int lineId, CancellationToken ct)
    {
        if (!await _db.Lines.AnyAsync(l => l.Id == lineId, ct))
        {
            return NotFound();
        }

        var nodes = await _db.Nodes.AsNoTracking()
            .Where(n => n.LineId == lineId && n.NodeByteId != null)
            .OrderBy(n => n.NodeByteId)
            .ToListAsync(ct);
        var health = _health.Nodes().Where(n => n.LineId == lineId).ToDictionary(n => n.NodeByteId);

        return nodes.Select(n =>
        {
            health.TryGetValue(n.NodeByteId!.Value, out var h);
            return new LineNodeView(
                n.Id, n.NodeByteId.Value, n.Name,
                h?.State ?? Liveness.Unknown,
                h?.LastSeen ?? (n.LastOnline is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null),
                h?.LastStatus?.KnownBatteryPercent ?? (float?)n.BatteryLevel,
                h?.LastStatus?.State.ToString(),
                h?.LastStatus?.Errors.ToString(),
                n.PhysicalId);
        }).ToList();
    }

    [HttpPatch("{lineId:int}")]
    [Authorize(Policy = Policies.DeviceManagement)]
    public async Task<IActionResult> Rename(int lineId, RenameLineRequest request, CancellationToken ct)
    {
        var line = await _db.Lines.FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null)
        {
            return NotFound();
        }

        line.Name = request.Name.Trim();
        _db.Audit(User, "line.rename", "Line", lineId.ToString(), new { line.Name });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Gỡ tuyến: thu hồi tài khoản MQTT của NM, giữ dữ liệu lịch sử, id tuyến không được cấp lại.</summary>
    [HttpPost("{lineId:int}/retire")]
    [Authorize(Policy = Policies.SystemAdministration)]
    public async Task<IActionResult> Retire(int lineId, CancellationToken ct)
    {
        if (!await _provisioning.RetireAsync(lineId, ct))
        {
            return NotFound();
        }

        _db.Audit(User, "line.retire", "Line", lineId.ToString());
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
