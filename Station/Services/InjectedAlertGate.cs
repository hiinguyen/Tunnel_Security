using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Station.Models;

namespace Station.Services;

/// <summary>
/// Chỉ cho phép thông báo những cảnh báo do AlertInjector phát. Hỏi cổng điều khiển cục bộ của
/// NodePublisherSim (GET /state) để biết node + cảm biến nào đang bị ép giá trị; giá trị vượt ngưỡng
/// tự nhiên của mô phỏng không được tính là cảnh báo cần thông báo.
/// </summary>
public sealed class InjectedAlertGate : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    private const int DefaultPort = 5199;

    private readonly HttpClient _http;
    private readonly DispatcherQueue? _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private HashSet<string> _active = new();
    private int _started;

    /// <summary>Thể hiện dùng chung; lần truy cập đầu phải diễn ra trên UI thread.</summary>
    public static InjectedAlertGate Shared { get; } = new();

    private InjectedAlertGate()
    {
        var raw = Environment.GetEnvironmentVariable("SIM_CONTROL_PORT");
        var port = int.TryParse(raw, out var p) && p > 0 ? p : DefaultPort;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(2)
        };
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>Tập cảnh báo đang bị ép thay đổi (raise trên UI thread).</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Task.Run(PollLoopAsync);
        }
    }

    public bool IsActive(Alert alert)
    {
        var kind = KindOf(alert.Category);
        if (kind is null) return false;

        var active = Volatile.Read(ref _active);
        return Keys(alert).Any(k => active.Contains($"{k}|{kind}"));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var next = await FetchActiveAsync();
            var current = Volatile.Read(ref _active);
            if (!next.SetEquals(current))
            {
                Volatile.Write(ref _active, next);
                _dispatcher?.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
            }

            try { await Task.Delay(PollInterval, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Sim không chạy hoặc lỗi mạng: coi như không có cảnh báo giả nào đang phát.
    private async Task<HashSet<string>> FetchActiveAsync()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = await _http.GetStreamAsync("state", _cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: _cts.Token);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodes)) return result;

            foreach (var node in nodes.EnumerateArray())
            {
                if (!node.TryGetProperty("overrides", out var overrides) || overrides.GetArrayLength() == 0) continue;

                var keys = new List<string>();
                if (node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    keys.Add(name.GetString()!);
                if (node.TryGetProperty("nodeId", out var id) && id.ValueKind == JsonValueKind.Number)
                    keys.Add(id.GetInt32().ToString());

                foreach (var o in overrides.EnumerateArray())
                {
                    var sensor = o.GetProperty("sensor").GetString();
                    if (sensor is null) continue;
                    foreach (var key in keys) result.Add($"{key}|{sensor}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            // Bỏ qua: sim chưa bật.
        }

        return result;
    }

    private static IEnumerable<string> Keys(Alert alert)
    {
        if (!string.IsNullOrEmpty(alert.NodeName)) yield return alert.NodeName;
        if (!string.IsNullOrEmpty(alert.NodeId)) yield return alert.NodeId;
    }

    private static string? KindOf(AlertCategory category) => category switch
    {
        AlertCategory.Light => "light",
        AlertCategory.WaterLevel => "water",
        AlertCategory.Temperature => "temp",
        AlertCategory.Humidity => "hum",
        AlertCategory.Radar => "radar",
        _ => null
    };
}
