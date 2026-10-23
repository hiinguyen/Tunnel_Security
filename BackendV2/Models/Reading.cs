namespace BackendV2.Models
{
    public enum ReadingLevel
    {
        Normal,
        Warning,
        Critical
    }

    /// <summary>
    /// Time-series sensor reading — lives in TimeSeriesDbContext (PostgreSQL/TimescaleDB),
    /// not in the SQL Server AppDbContext. SensorId/NodeId are soft references
    /// (no cross-database FK) mirroring Backend's SensorReadingTs.
    /// </summary>
    public class Reading
    {
        public int Id { get; set; }
        public string SensorId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public double Value { get; set; }
        public string Description { get; set; }

        /// <summary>Soft reference — node this reading came from</summary>
        public string? NodeId { get; set; }

        /// <summary>Wire-protocol byte ID of the node (1-10)</summary>
        public short? NodeByteId { get; set; }

        /// <summary>Wire-protocol byte ID of the sensor (1-7)</summary>
        public short? SensorByteId { get; set; }

        /// <summary>Rolling sequence counter 0-255 from the node firmware</summary>
        public short? Seq { get; set; }

        public ReadingLevel Level { get; set; } = ReadingLevel.Normal;

        public bool Crc8Ok { get; set; } = true;
    }
}
