namespace BackendV2.Models
{
    public enum CameraProtocol
    {
        RTSP,
        HTTP,
        WebSocket
    }

    public enum CameraStatus
    {
        Online,
        Offline,
        Error
    }

    public class Camera
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string NodeId { get; set; }

        public string? StreamUrl { get; set; }
        public CameraProtocol Protocol { get; set; } = CameraProtocol.RTSP;
        public CameraStatus Status { get; set; } = CameraStatus.Offline;

        // Thông số kỹ thuật
        public string? Resolution { get; set; }  // e.g. "1280x720"
        public int? Fps { get; set; }
        public string? Codec { get; set; }       // e.g. "H.264", "H.265"

        // Tính năng
        public bool IrEnabled { get; set; }
        public bool HdrEnabled { get; set; }
        public bool IsRecording { get; set; }

        public DateTime? LastFrameTime { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
