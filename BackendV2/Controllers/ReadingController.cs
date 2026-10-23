using BackendV2.Data;
using BackendV2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using BackendV2.Hubs;

namespace BackendV2.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ReadingController : ControllerBase
    {
        private readonly TimeSeriesDbContext _db;
        private readonly IHubContext<SensorHub> _hub;

        public ReadingController(TimeSeriesDbContext db, IHubContext<SensorHub> hub)
        {
            _db = db;
            _hub = hub;
        }

        [HttpGet]
        public async Task<ActionResult<List<Reading>>> GetAll()
        {
            var readings = await _db.Readings.ToListAsync();
            return Ok(readings);
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

            // Notify clients about the new reading
            await _hub.Clients.All.SendAsync("NewReading", reading);

            return CreatedAtAction(nameof(GetById), new { id = reading.Id }, reading);
        }
    }
}