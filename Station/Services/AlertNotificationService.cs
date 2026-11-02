using System;
using System.Collections.Generic;
using System.Linq;
using Station.Models;

namespace Station.Services;

/// <summary>
/// Trung tâm thông báo cảnh báo dùng chung cho mọi cửa sổ Station.
/// Nghe <see cref="IDataService.AlertGenerated"/>, gộp các cảnh báo lặp của cùng một cảm biến
/// (DataService phát một cảnh báo cho mỗi mẫu đo vượt ngưỡng) và giữ hàng đợi chờ xác nhận
/// để badge ở màn hình giám sát hiển thị. Sự kiện luôn được raise trên UI thread.
/// </summary>
public sealed class AlertNotificationService
{
    // Sau khi xác nhận, cùng một cảm biến vẫn đang vượt ngưỡng sẽ không bật lại ngay.
    private static readonly TimeSpan AcknowledgeSuppression = TimeSpan.FromSeconds(60);

    public static AlertNotificationService Instance { get; } = new();

    private readonly List<Alert> _pending = new();
    private readonly Dictionary<string, DateTimeOffset> _suppressedUntil = new();
    private bool _started;
    private InjectedAlertGate? _gate;

    private AlertNotificationService() { }

    /// <summary>Cảnh báo đang chờ xác nhận, mức nghiêm trọng cao xếp trước.</summary>
    public IReadOnlyList<Alert> Pending => _pending;

    /// <summary>Có cảnh báo mới (hoặc cảnh báo cũ tái diễn sau khi hết thời gian chặn).</summary>
    public event EventHandler<Alert>? AlertRaised;

    /// <summary>Hàng đợi chờ xác nhận thay đổi (thêm, cập nhật hoặc xác nhận).</summary>
    public event EventHandler? PendingChanged;

    public void Start()
    {
        if (_started) return;
        _started = true;
        DataServiceLocator.Current.AlertGenerated += OnAlertGenerated;

        // Với backend thật, chỉ thông báo cảnh báo do AlertInjector phát (giá trị vượt ngưỡng tự nhiên
        // của mô phỏng bị bỏ qua). Chế độ mock không có cổng điều khiển nên giữ hành vi cũ.
        if (DataServiceLocator.Current is not MockDataService)
        {
            _gate = InjectedAlertGate.Shared;
            _gate.Changed += OnInjectionChanged;
            _gate.Start();
        }
    }

    // AlertInjector vừa phát thêm hoặc xóa cảnh báo giả: bỏ khỏi hàng đợi những cảnh báo đã bị xóa.
    private void OnInjectionChanged(object? sender, EventArgs e)
    {
        if (_gate is null) return;
        if (_pending.RemoveAll(a => !_gate.IsActive(a)) > 0)
        {
            PendingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Acknowledge(Alert alert)
    {
        if (!_pending.Remove(alert)) return;

        _suppressedUntil[KeyOf(alert)] = DateTimeOffset.Now + AcknowledgeSuppression;
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAlertGenerated(object? sender, AlertGeneratedEventArgs e)
    {
        var alert = e.Alert;
        if (alert.Severity == AlertSeverity.Low) return;
        if (_gate is not null && !_gate.IsActive(alert)) return;

        var key = KeyOf(alert);
        if (_suppressedUntil.TryGetValue(key, out var until))
        {
            if (DateTimeOffset.Now < until) return;
            _suppressedUntil.Remove(key);
        }

        var existingIndex = _pending.FindIndex(a => KeyOf(a) == key);
        if (existingIndex >= 0)
        {
            // Cùng cảm biến đang cảnh báo: chỉ làm mới giá trị, không tăng bộ đếm, không nháy lại.
            _pending[existingIndex] = alert;
            SortPending();
            PendingChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _pending.Add(alert);
        SortPending();
        PendingChanged?.Invoke(this, EventArgs.Empty);
        AlertRaised?.Invoke(this, alert);
    }

    private void SortPending()
    {
        var sorted = _pending
            .OrderByDescending(a => (int)a.Severity)
            .ThenBy(a => a.CreatedAt)
            .ToList();
        _pending.Clear();
        _pending.AddRange(sorted);
    }

    private static string KeyOf(Alert alert)
        => !string.IsNullOrEmpty(alert.SensorId) ? alert.SensorId : $"{alert.NodeId}:{alert.Category}";
}
