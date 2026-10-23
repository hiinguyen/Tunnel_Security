namespace BackendV2.Models
{
    public enum NodeStatus
    {
        Online,
        Warning,
        Critical,
        Offline,
        Maintenance
    }

    public class Node
    {
        public string Id { get; set; }
        public string? Code { get; set; }
        public string Name { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Description { get; set; }

        public NodeStatus Status { get; set; } = NodeStatus.Offline;
        public DateTime? LastOnline { get; set; }

        /// <summary>Wire-protocol byte ID của node (1-10) — dùng trong binary frame</summary>
        public byte? NodeByteId { get; set; }

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
