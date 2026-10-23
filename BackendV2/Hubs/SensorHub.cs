using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;


namespace BackendV2.Hubs
{
    [Authorize]
    public class SensorHub : Hub
    {
    }
}
