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
        public string SensorId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public double Value { get; set; }
        public string Description { get; set; } = string.Empty;

        /// <summary>Soft reference — node this reading came from</summary>
        public string? NodeId { get; set; }

        /// <summary>Line (gateway) the node belongs to; node byte IDs are only unique per line.</summary>
        public short? LineId { get; set; }

        /// <summary>Wire-protocol node ID (1..254) within the line</summary>
        public short? NodeByteId { get; set; }

        /// <summary>TLV type byte of the sensor (0x01-0x7F)</summary>
        public short? SensorByteId { get; set; }

        /// <summary>TLV ordinal (header byte 2): which sensor of that type on the node</summary>
        public short? Seq { get; set; }

        public ReadingLevel Level { get; set; } = ReadingLevel.Normal;

        public bool Crc8Ok { get; set; } = true;
    }
}
