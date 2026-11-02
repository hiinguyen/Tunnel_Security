namespace BackendV2.Models
{
    public enum SensorType
    {
        Radar,          // Radar biến dạng (mm)
        Vibration,      // Rung động (mm/s)
        SmokeFire,      // Khói/Lửa (%)
        Temperature,    // Nhiệt độ (°C)
        Humidity,       // Độ ẩm (%)
        Gas,            // Khí gas (ppm)
        Pressure,       // Áp suất
        WaterLevel,     // Mực nước
        Motion,         // Chuyển động
        Light           // Ánh sáng (lx)
    }

    public class Sensor
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public SensorType Type { get; set; }
        public string Description { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;

        /// <summary>Byte loại trong header TLV (0x01-0x7F cảm biến).</summary>
        public byte? SensorByteId { get; set; }

        /// <summary>Số thứ tự trong header TLV, phân biệt nhiều cảm biến cùng loại trên một node.</summary>
        public byte? Ordinal { get; set; }

        public string Unit { get; set; } = string.Empty;

        // Ngưỡng cảnh báo
        public double? WarningThreshold { get; set; }
        public double? CriticalThreshold { get; set; }

        // Giá trị hiện tại (cache — cập nhật mỗi reading)
        public double? CurrentValue { get; set; }
        public string? CurrentLevel { get; set; } // "Normal" | "Warning" | "Critical"
        public DateTime? LastReading { get; set; }

        // Cấu hình
        public bool IsEnabled { get; set; } = true;
        public int SamplingRate { get; set; } = 1; // Hz
        public double? SamplingRateHz { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
