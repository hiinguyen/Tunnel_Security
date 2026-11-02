using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;


namespace BackendV2.Hubs
{
    /// <summary>
    /// Kênh realtime tới Station. Mỗi connection thuộc đúng một phạm vi nhận "NewReading":
    /// "all" (mặc định, giữ tương thích với Station hiện tại), "line:{k}" hoặc "node:{k}:{n}".
    /// </summary>
    [Authorize]
    public class SensorHub : Hub
    {
        public const string AllGroup = "all";

        private const string ScopeKey = "scope";

        public static string LineGroup(int lineId) => $"line-{lineId}";

        public static string NodeGroup(int lineId, int nodeId) => $"node-{lineId}-{nodeId}";

        public override async Task OnConnectedAsync()
        {
            await JoinAsync(AllGroup);
            await base.OnConnectedAsync();
        }

        /// <summary>Chọn phạm vi nhận reading: "all", "line:{k}", "node:{k}:{n}" hoặc "none".</summary>
        public async Task SetScope(string scope)
        {
            var group = ParseScope(scope)
                ?? throw new HubException($"Invalid scope '{scope}'. Use all, none, line:{{k}} or node:{{k}}:{{n}}");

            if (Context.Items.TryGetValue(ScopeKey, out var current) && current is string old)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, old);
                Context.Items.Remove(ScopeKey);
            }

            if (group.Length > 0)
            {
                await JoinAsync(group);
            }
        }

        private async Task JoinAsync(string group)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
            Context.Items[ScopeKey] = group;
        }

        private static string? ParseScope(string scope)
        {
            var parts = scope.Split(':');
            return parts switch
            {
                ["all"] => AllGroup,
                ["none"] => string.Empty,
                ["line", var l] when int.TryParse(l, out var line) && line is >= 1 and <= 254 => LineGroup(line),
                ["node", var l, var n] when int.TryParse(l, out var line) && line is >= 1 and <= 254
                                             && int.TryParse(n, out var node) && node is >= 1 and <= 254 =>
                    NodeGroup(line, node),
                _ => null
            };
        }
    }
}
