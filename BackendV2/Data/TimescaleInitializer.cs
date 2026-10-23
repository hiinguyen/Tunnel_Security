using Microsoft.EntityFrameworkCore;

namespace BackendV2.Data;

/// <summary>
/// Runs after EnsureCreated() to convert the plain PostgreSQL "Readings" table into
/// a TimescaleDB hypertable (partitioned by Timestamp) — mirrors Backend's
/// TimescaleInitializer. Safe to call multiple times (IF NOT EXISTS checks).
/// If the TimescaleDB extension is not installed the call is silently skipped so
/// the app can still start (just without time-series partitioning).
/// </summary>
public static class TimescaleInitializer
{
    private const string Table = "Readings";
    private const string TimeCol = "Timestamp";
    private const string ChunkInterval = "1 day";

    public static async Task InitializeAsync(TimeSeriesDbContext db, ILogger logger)
    {
        // 1. Create tables if they don't exist (idempotent)
        await db.Database.EnsureCreatedAsync();

        // 2. Enable TimescaleDB extension (requires superuser the first time)
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;");
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Timescale] Could not enable extension: {Msg} — " +
                              "hypertable partitioning skipped", ex.Message);
            return;
        }

        // 3. Convert the Readings table to a hypertable
        try
        {
            await db.Database.ExecuteSqlRawAsync($"""
                SELECT create_hypertable(
                    '"{Table}"', '{TimeCol}',
                    chunk_time_interval => INTERVAL '{ChunkInterval}',
                    if_not_exists       => TRUE,
                    migrate_data        => TRUE
                );
                """);

            logger.LogInformation("[Timescale] Hypertable ready: {Table} (chunk={Chunk})",
                Table, ChunkInterval);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Timescale] create_hypertable({Table}) failed: {Msg}",
                Table, ex.Message);
        }
    }
}
