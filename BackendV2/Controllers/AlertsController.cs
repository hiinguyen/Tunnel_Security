using System.ComponentModel.DataAnnotations;
using BackendV2.Auth;
using BackendV2.Data;
using BackendV2.Gateway.Alerts;
using BackendV2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Controllers;

public sealed record IncidentNoteRequest([StringLength(1000)] string? Note);

/// <summary>Cảnh báo: liệt kê, xác nhận, đóng. Danh sách có giới hạn số dòng để không trả về toàn bộ lịch sử.</summary>
[ApiController]
[Route("api/alerts")]
[Authorize]
public class AlertsController : ControllerBase
{
    private const int MaxLimit = 1000;

    private readonly AppDbContext _db;
    private readonly AlertEngine _engine;

    public AlertsController(AppDbContext db, AlertEngine engine)
    {
        _db = db;
        _engine = engine;
    }

    [HttpGet]
    public async Task<ActionResult<List<Alert>>> Get(
        [FromQuery] AlertState? state, [FromQuery] bool activeOnly = false, [FromQuery] int? lineId = null,
        [FromQuery] Guid? incidentId = null, [FromQuery, Range(1, MaxLimit)] int limit = 200, CancellationToken ct = default)
    {
        var query = _db.Alerts.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(a => a.State != AlertState.Resolved);
        }

        if (state is { } s)
        {
            query = query.Where(a => a.State == s);
        }

        if (lineId is { } line)
        {
            query = query.Where(a => a.LineId == line);
        }

        if (incidentId is { } incident)
        {
            query = query.Where(a => a.IncidentId == incident);
        }

        return await query.OrderByDescending(a => a.CreatedAt).Take(limit).ToListAsync(ct);
    }

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = Policies.AlertManagement)]
    public async Task<ActionResult<Alert>> Acknowledge(Guid id) =>
        await _engine.AcknowledgeAsync(id, User.Actor()) is { } alert ? Ok(alert) : NotFound();

    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = Policies.AlertManagement)]
    public async Task<ActionResult<Alert>> Resolve(Guid id) =>
        await _engine.ResolveAsync(id, User.Actor()) is { } alert ? Ok(alert) : NotFound();
}

/// <summary>Tình huống: liệt kê, chuyển sang đang xử lý, đóng.</summary>
[ApiController]
[Route("api/incidents")]
[Authorize]
public class IncidentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AlertEngine _engine;

    public IncidentsController(AppDbContext db, AlertEngine engine)
    {
        _db = db;
        _engine = engine;
    }

    [HttpGet]
    public async Task<ActionResult<List<Incident>>> Get(
        [FromQuery] IncidentState? state, [FromQuery] bool openOnly = false, [FromQuery, Range(1, 1000)] int limit = 100,
        CancellationToken ct = default)
    {
        var query = _db.Incidents.AsNoTracking();
        if (openOnly)
        {
            query = query.Where(i => i.State != IncidentState.Closed);
        }

        if (state is { } s)
        {
            query = query.Where(i => i.State == s);
        }

        return await query.OrderByDescending(i => i.CreatedAt).Take(limit).ToListAsync(ct);
    }

    [HttpPost("{id:guid}/handle")]
    [Authorize(Policy = Policies.AlertManagement)]
    public async Task<ActionResult<Incident>> Handle(Guid id, IncidentNoteRequest request) =>
        await _engine.SetIncidentStateAsync(id, IncidentState.Handling, User.Actor(), request.Note) is { } incident
            ? Ok(incident)
            : NotFound();

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = Policies.AlertManagement)]
    public async Task<ActionResult<Incident>> Close(Guid id, IncidentNoteRequest request) =>
        await _engine.SetIncidentStateAsync(id, IncidentState.Closed, User.Actor(), request.Note) is { } incident
            ? Ok(incident)
            : NotFound();
}
