using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Station.Models;
using Station.Services;
using System;
using System.Linq;
using Windows.UI;

namespace Station.Dialogs
{
    public sealed partial class AlertVideoDialog : ContentDialog
    {
        private readonly Alert _alert;
        private bool _acknowledged = false;

        public bool WasAcknowledged => _acknowledged;

        public AlertVideoDialog(Alert alert)
        {
            _alert = alert;
            this.InitializeComponent();
            RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
            PopulateAlertInfo();
        }

        private void PopulateAlertInfo()
        {
            var mock = MockDataService.Instance;
            var camera = _alert.CameraId != null
                ? mock.Cameras.FirstOrDefault(c => c.CameraId == _alert.CameraId)
                : null;

            // Header
            AlertTitleText.Text = _alert.Title;
            CameraIdText.Text = _alert.CameraId ?? _alert.NodeId ?? "-";
            AlertTimeText.Text = _alert.CreatedAt.ToString("HH:mm:ss  dd/MM/yyyy");
            VideoCameraName.Text = camera != null
                ? $"{camera.CameraId} - {camera.Location}"
                : (_alert.CameraId ?? "Camera không xác định");
            CameraDetailText.Text = camera != null
                ? $"{camera.CameraId} · {camera.Location}"
                : (_alert.CameraId ?? "-");

            // Severity styling
            ApplySeverityStyle(_alert.Severity);

            // Detail panel
            AlertTitleDetail.Text = _alert.Title;
            AlertDescriptionText.Text = string.IsNullOrEmpty(_alert.Description) ? "Không có mô tả." : _alert.Description;
            AlertLocationText.Text = string.IsNullOrEmpty(_alert.NodeName)
                ? (_alert.NodeId ?? "-")
                : $"{_alert.NodeName} ({_alert.NodeId})";
            AlertTimeDetailText.Text = _alert.CreatedAt.ToString("HH:mm:ss - dd/MM/yyyy");

            // Duration
            var elapsed = DateTimeOffset.Now - _alert.CreatedAt;
            int minutes = (int)elapsed.TotalMinutes;
            AlertDurationText.Text = minutes <= 0
                ? "Vừa xảy ra"
                : $"Đang diễn ra · {minutes} phút trước";

            AlertCategoryText.Text = "Phát hiện bởi hệ thống · Cảnh báo tự động";
        }

        private static Brush Res(string key) => (Brush)Station.Services.ThemeBrushes.Get(key);

        // Severity uses the DESIGN palette; the label always carries a marker so it never relies on color alone.
        private void ApplySeverityStyle(AlertSeverity severity)
        {
            var (label, marker, fg, bg) = severity switch
            {
                AlertSeverity.Critical => ("KHẨN CẤP", "▲", "DkRedBrush", "DkRedBgBrush"),
                AlertSeverity.High => ("NGUY HIỂM", "▲", "DkOrangeBrush", "DkOrangeBgBrush"),
                AlertSeverity.Medium => ("TRUNG BÌNH", "▲", "DkYellowBrush", "DkYellowBgBrush"),
                _ => ("THẤP", "●", "DkGreenBrush", "DkGreenBgBrush")
            };

            SeverityText.Text = $"{marker} {label}";
            SeverityDetailText.Text = $"{marker} {label}";
            SeverityBadge.Background = Res(bg);
            SeverityBadge.BorderBrush = Res(fg);
            SeverityText.Foreground = Res(fg);
            SeverityDetailText.Foreground = Res(fg);
            SeverityDetailBlock.Background = Res(bg);
            SeverityDetailBlock.BorderBrush = Res(fg);
            AlertDurationText.Foreground = Res(fg);
            AlertZoneOverlay.BorderBrush = Res(fg);
        }

        private void AcknowledgeButton_Click(object sender, RoutedEventArgs e)
        {
            _acknowledged = true;
            this.Hide();
        }

        private void DismissButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[AlertDialog] Snapshot taken for {_alert.CameraId ?? _alert.NodeId}");
        }
    }
}
