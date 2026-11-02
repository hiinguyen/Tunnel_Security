using BackendV2.Hubs;
using BackendV2.Models;
using Microsoft.AspNetCore.SignalR;

namespace BackendV2.Gateway.Realtime;

/// <summary>
/// Phát sự kiện realtime tới Station qua SignalR. Reading chỉ tới client đang xem đúng phạm vi
/// (toàn trạm, một tuyến, hoặc một node, xem <see cref="SensorHub.SetScope"/>) để lưu lượng không tăng theo số node.
/// Các sự kiện trạng thái và cảnh báo ít, gửi cho mọi client.
/// </summary>
public sealed class RealtimeNotifier
{
    public const string NewReading = "NewReading";
    public const string GatewayChanged = "GatewayChanged";
    public const string NodeChanged = "NodeChanged";
    public const string AlertChanged = "AlertChanged";
    public const string IncidentChanged = "IncidentChanged";
    public const string SafetyStatus = "SafetyStatus";
    public const string PendingGateway = "PendingGateway";
    public const string PendingDevice = "PendingDevice";
    public const string NodeJoined = "NodeJoined";

    private readonly IHubContext<SensorHub> _hub;
    private readonly ILogger<RealtimeNotifier> _logger;

    public RealtimeNotifier(IHubContext<SensorHub> hub, ILogger<RealtimeNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public Task ReadingAsync(Reading reading)
    {
        var groups = new List<string>(3) { SensorHub.AllGroup };
        if (reading.LineId is { } line)
        {
            groups.Add(SensorHub.LineGroup(line));
            if (reading.NodeByteId is { } node)
            {
                groups.Add(SensorHub.NodeGroup(line, node));
            }
        }

        // Mỗi connection chỉ thuộc đúng một group (SetScope), nên gửi tới nhiều group không bị trùng.
        return SafeSend(() => _hub.Clients.Groups(groups).SendAsync(NewReading, reading), NewReading);
    }

    public Task BroadcastAsync(string method, object payload) =>
        SafeSend(() => _hub.Clients.All.SendAsync(method, payload), method);

    private async Task SafeSend(Func<Task> send, string method)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[SignalR] {Method} broadcast failed: {Message}", method, ex.Message);
        }
    }
}
