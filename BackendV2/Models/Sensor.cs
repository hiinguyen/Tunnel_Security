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
        public string Id { get; set; }
        public string Name { get; set; }
        public SensorType Type { get; set; }
        public string Description { get; set; }
        public string NodeId { get; set; }

        /// <summary>Wire-protocol byte ID của sensor trong node (1-7)</summary>
        public byte? SensorByteId { get; set; }

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
