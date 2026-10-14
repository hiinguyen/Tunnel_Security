namespace BackendV2.Models
{
    public enum NodeStatus
    {
        Online,
        Warning,
        Critical,
        Offline,
        Maintenance,

        /// <summary>NM của tuyến mất kết nối nên không biết node còn sống hay không.</summary>
        Unknown
    }

    public class Node
    {
        public string Id { get; set; } = string.Empty;
        public string? Code { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Description { get; set; } = string.Empty;

        public NodeStatus Status { get; set; } = NodeStatus.Offline;
        public DateTime? LastOnline { get; set; }

        /// <summary>Tuyến chứa node; null với node demo cũ chưa thuộc tuyến nào.</summary>
        public int? LineId { get; set; }

        /// <summary>Node ID trong khung nhị phân (1..254), chỉ duy nhất trong một tuyến.</summary>
        public byte? NodeByteId { get; set; }

        /// <summary>ID vật lý của thiết bị (uint32) đã xác thực khi tham gia bằng lệnh A1.</summary>
        public long? PhysicalId { get; set; }

        // Thông tin thiết bị
        public string? HardwareId { get; set; }
        public string? Mac { get; set; }
        public string? IpAddress { get; set; }
        public string? FirmwareVersion { get; set; }
        public bool IsHub { get; set; }

        // Pin & tín hiệu
        public double? BatteryLevel { get; set; }
        public int? RSSI { get; set; }

        // Camera gắn trên nút (nếu có)
        public string? CameraId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
