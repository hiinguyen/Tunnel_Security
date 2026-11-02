using System.Net.Http.Json;
using System.Text.Json;

namespace AlertInjector;

public sealed record OverrideInfo(string Sensor, double Value, double? SecondsLeft);

public sealed record NodeInfo(int Gateway, int Line, int Node, int? NodeId, string? Name, bool Joined, List<OverrideInfo> Overrides)
{
    /// <summary>Tên hố ga trên bản đồ dashboard (khớp tên node trong phần mềm); chỉ khi sim chạy topology tổng hợp mới dùng nhãn tuyến/số.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : NodeId is { } id ? $"Tuyến {Line} - N{id}" : $"Tuyến {Line} - node {Node}";

    public string Label => NodeId is null ? $"{DisplayName} (chưa join)" : DisplayName;
}

/// <summary>Client HTTP tới cổng điều khiển của NodePublisherSim.</summary>
public sealed class SimClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public SimClient(Uri baseAddress) => _http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(3) };

    private sealed record StateResponse(List<NodeInfo> Nodes);

    public async Task<List<NodeInfo>> GetNodesAsync() =>
        (await _http.GetFromJsonAsync<StateResponse>("state", Json))?.Nodes ?? [];

    public Task InjectAsync(NodeInfo node, string sensor, string level, double? value, int durationSeconds) =>
        PostAsync("inject", new { node.Gateway, node.Node, sensor, level, value, durationSeconds });

    public Task ClearAsync(NodeInfo node, string? sensor = null) =>
        PostAsync("clear", new { node.Gateway, node.Node, sensor });

    public Task ClearAllAsync() => PostAsync("clear", new { });

    private async Task PostAsync(string path, object body)
    {
        using var response = await _http.PostAsJsonAsync(path, body, Json);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        }
    }
}
