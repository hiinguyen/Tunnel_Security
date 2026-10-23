using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using BackendV2.Services;


namespace BackendV2.Middlewares;

public class CameraIngestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CameraRelayRegistry _registry;

    public CameraIngestMiddleware(RequestDelegate next, CameraRelayRegistry registry)
    {
        _next = next;
        _registry = registry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;

        var isIngestRequest = path is not null
            && path.StartsWith("/ws/camera/")
            && path.EndsWith("/ingest")
            && context.WebSockets.IsWebSocketRequest;

        if (!isIngestRequest)
        {
            await _next(context);
            return;
        }

        var segments = path!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cameraId = segments[2];

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        var buffer = new byte[8192];

        while (socket.State == WebSocketState.Open)
        {
            using var frameStream = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer, context.RequestAborted);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);
                    return;
                }

                frameStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            await _registry.BroadcastFrameAsync(cameraId, frameStream.ToArray(), context.RequestAborted);
        }
    }
}