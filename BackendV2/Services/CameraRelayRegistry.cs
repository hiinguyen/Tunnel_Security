using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace BackendV2.Services
{
    /// <summary>
    /// Chuyển bản tin từ nguồn camera tới mọi viewer của camera đó.
    /// Binary là một JPEG (được giữ lại làm frame mới nhất), Text là JSON khung YOLO (chỉ chuyển tiếp).
    /// Mỗi viewer có một khóa gửi vì WebSocket không cho hai lệnh SendAsync chạy chồng nhau.
    /// </summary>
    public class CameraRelayRegistry
    {
        public const int MaxMessageBytes = 2 * 1024 * 1024;

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, SemaphoreSlim>> _viewersByCamera = new();
        private readonly ConcurrentDictionary<string, byte[]> _latestFrameByCamera = new();

        public void AddViewer(string cameraId, WebSocket socket) =>
            _viewersByCamera.GetOrAdd(cameraId, _ => new())[socket] = new SemaphoreSlim(1, 1);

        public void RemoveViewer(string cameraId, WebSocket socket)
        {
            if (_viewersByCamera.TryGetValue(cameraId, out var viewers))
            {
                viewers.TryRemove(socket, out _);
            }
        }

        /// <summary>Frame mới nhất của camera, để viewer vừa mở thấy hình ngay thay vì màn hình đen.</summary>
        public bool TryGetLatestFrame(string cameraId, out byte[] frame)
        {
            if (_latestFrameByCamera.TryGetValue(cameraId, out var cached))
            {
                frame = cached;
                return true;
            }

            frame = [];
            return false;
        }

        public async Task SendLatestFrameAsync(string cameraId, WebSocket socket, CancellationToken ct)
        {
            if (TryGetLatestFrame(cameraId, out var frame)
                && _viewersByCamera.TryGetValue(cameraId, out var viewers)
                && viewers.TryGetValue(socket, out var gate))
            {
                await SendAsync(socket, gate, frame, WebSocketMessageType.Binary, ct);
            }
        }

        public async Task BroadcastAsync(string cameraId, byte[] data, WebSocketMessageType type, CancellationToken ct)
        {
            if (type == WebSocketMessageType.Binary)
            {
                _latestFrameByCamera[cameraId] = data;
            }

            if (!_viewersByCamera.TryGetValue(cameraId, out var viewers))
            {
                return;
            }

            foreach (var (socket, gate) in viewers)
            {
                if (socket.State != WebSocketState.Open)
                {
                    viewers.TryRemove(socket, out _);
                    continue;
                }

                try
                {
                    await SendAsync(socket, gate, data, type, ct);
                }
                catch (Exception)
                {
                    viewers.TryRemove(socket, out _);
                }
            }
        }

        private static async Task SendAsync(WebSocket socket, SemaphoreSlim gate, byte[] data, WebSocketMessageType type, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                await socket.SendAsync(data, type, endOfMessage: true, ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
