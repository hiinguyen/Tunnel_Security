using System.ComponentModel.DataAnnotations;
using BackendV2.Data;
using BackendV2.Gateway.Realtime;
using BackendV2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ReadingController : ControllerBase
    {
        private const int MaxLimit = 5000;

        private readonly TimeSeriesDbContext _db;
        private readonly RealtimeNotifier _notifier;

        public ReadingController(TimeSeriesDbContext db, RealtimeNotifier notifier)
        {
            _db = db;
            _notifier = notifier;
        }

        /// <summary>
        /// Readings mới nhất trước, có bộ lọc và giới hạn số dòng: bảng chuỗi thời gian tăng hàng nghìn dòng mỗi giây
        /// nên không bao giờ trả về toàn bộ.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<Reading>>> GetAll(
            [FromQuery] string? sensorId,
            [FromQuery] string? nodeId,
            [FromQuery] short? lineId,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery, Range(1, MaxLimit)] int limit = 500,
            CancellationToken ct = default)
        {
            var query = _db.Readings.AsNoTracking();
            if (!string.IsNullOrEmpty(sensorId))
            {
                query = query.Where(r => r.SensorId == sensorId);
            }

            if (!string.IsNullOrEmpty(nodeId))
            {
                query = query.Where(r => r.NodeId == nodeId);
            }

            if (lineId is { } line)
            {
                query = query.Where(r => r.LineId == line);
            }

            if (from is { } start)
            {
                var utc = start.ToUniversalTime();
                query = query.Where(r => r.Timestamp >= utc);
            }

            if (to is { } end)
            {
                var utc = end.ToUniversalTime();
                query = query.Where(r => r.Timestamp < utc);
            }

            return await query.OrderByDescending(r => r.Timestamp).Take(limit).ToListAsync(ct);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Reading>> GetById(int id)
        {
            // Reading's PK is composite (Id, Timestamp) — TimescaleDB requires the
            // partition column in every key — so FindAsync(id) alone can't resolve it.
            var reading = await _db.Readings.FirstOrDefaultAsync(r => r.Id == id);
            if (reading is null)
            {
                return NotFound();
            }
            return Ok(reading);
        }

        [HttpPost]
        public async Task<ActionResult<Reading>> Create(Reading reading)
        {
            _db.Readings.Add(reading);
            await _db.SaveChangesAsync();

            await _notifier.ReadingAsync(reading);

            return CreatedAtAction(nameof(GetById), new { id = reading.Id }, reading);
        }
    }
}
