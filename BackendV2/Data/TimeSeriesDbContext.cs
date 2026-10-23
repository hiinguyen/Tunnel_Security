using BackendV2.Models;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Data;

/// <summary>
/// DbContext targeting PostgreSQL + TimescaleDB — mirrors Backend's TimeSeriesDbContext.
/// Holds only the time-series Reading table; Node/Camera/Sensor topology stays in
/// AppDbContext (SQL Server). SensorId/NodeId on Reading are soft references — no FK
/// across databases.
/// </summary>
public class TimeSeriesDbContext : DbContext
{
    public TimeSeriesDbContext(DbContextOptions<TimeSeriesDbContext> options) : base(options) { }

    public DbSet<Reading> Readings => Set<Reading>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<ReadingLevel>("reading_level");

        // TimescaleDB requires all unique constraints to include the partition
        // column (Timestamp), so we use a composite PK (Id, Timestamp).
        modelBuilder.Entity<Reading>(e =>
        {
            e.HasKey(x => new { x.Id, x.Timestamp });
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.Timestamp).HasColumnType("timestamp with time zone").IsRequired();

            e.HasIndex(x => new { x.SensorId, x.Timestamp });
            e.HasIndex(x => new { x.NodeId, x.Timestamp });
        });
    }
}
