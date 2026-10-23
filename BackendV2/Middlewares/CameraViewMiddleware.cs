using System.Net.WebSockets;
using BackendV2.Services;

namespace BackendV2.Middlewares
{
    public class CameraViewMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly CameraRelayRegistry _registry;

        public CameraViewMiddleware(RequestDelegate next, CameraRelayRegistry registry)
        {
            _next = next;
            _registry = registry;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value;

            var isViewRequest = path is not null
                && path.StartsWith("/ws/camera/")
                && path.EndsWith("/view")
                && context.WebSockets.IsWebSocketRequest;

            if (!isViewRequest)
            {
                await _next(context);
                return;
            }

            var segments = path!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cameraId = segments[2];

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _registry.AddViewer(cameraId, socket);

            if (_registry.TryGetLatestFrame(cameraId, out var latestFrame))
            {
                await socket.SendAsync(latestFrame, WebSocketMessageType.Binary, endOfMessage: true, context.RequestAborted);
            }

            var buffer = new byte[8192];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);
                        break;
                    }
                }
            }
            finally
            {
                _registry.RemoveViewer(cameraId, socket);
            }
        }
    }
}