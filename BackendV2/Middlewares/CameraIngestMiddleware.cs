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

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, context.RequestAborted);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);
                        return;
                    }

                    if (message.Length + result.Count > CameraRelayRegistry.MaxMessageBytes)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", context.RequestAborted);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                await _registry.BroadcastAsync(cameraId, message.ToArray(), result.MessageType, context.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The sender went away without a close handshake (gateway killed, link dropped): the normal end of a stream.
        }
    }
}