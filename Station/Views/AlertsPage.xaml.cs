using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Station.Dialogs;
using Station.Models;
using Station.Services;
using Station.ViewModels;
using System;

namespace Station.Views
{
    public sealed partial class AlertsPage : Page
    {
        public AlertsViewModel ViewModel { get; }

        public AlertsPage()
        {
            this.InitializeComponent();
            ViewModel = new AlertsViewModel();
            ApplySegmentState(CurrentViewButton, true);
            ApplySegmentState(HistoryViewButton, false);

            // Subscribe to live alerts from simulation
            MockDataService.Instance.AlertGenerated += OnLiveAlertGenerated;
        }

        // ── MockDataService integration ─────────────────────────────────

        private void OnLiveAlertGenerated(object? sender, AlertGeneratedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => ViewModel.AddLiveAlert(e.Alert));
        }

        // ── Header buttons ──────────────────────────────────────────────

        private static void ApplySegmentState(Button button, bool isActive)
        {
            if (isActive)
            {
                button.Background = (Microsoft.UI.Xaml.Media.Brush)Station.Services.ThemeBrushes.Get("DkBlueBgBrush");
                button.Foreground = (Microsoft.UI.Xaml.Media.Brush)Station.Services.ThemeBrushes.Get("DkBlueLightBrush");
                button.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Station.Services.ThemeBrushes.Get("DkBlueBorderBrush");
                button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.Foreground = (Microsoft.UI.Xaml.Media.Brush)Station.Services.ThemeBrushes.Get("DkTextSecondaryBrush");
                button.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        // ── Severity / state markers (shape + text, never color alone) ──

        private static Microsoft.UI.Xaml.Media.Brush Res(string key)
            => (Microsoft.UI.Xaml.Media.Brush)Station.Services.ThemeBrushes.Get(key);

        public static string SeverityMarker(AlertSeverity severity) => severity switch
        {
            AlertSeverity.Critical => "■",
            AlertSeverity.High => "▲",
            AlertSeverity.Medium => "▲",
            _ => "●"
        };

        public static Microsoft.UI.Xaml.Media.Brush SeverityBrush(AlertSeverity severity) => Res(severity switch
        {
            AlertSeverity.Critical => "DkRedBrush",
            AlertSeverity.High => "DkOrangeBrush",
            AlertSeverity.Medium => "DkYellowBrush",
            _ => "DkTextSecondaryBrush"
        });

        public static Microsoft.UI.Xaml.Media.Brush SeverityBgBrush(AlertSeverity severity) => Res(severity switch
        {
            AlertSeverity.Critical => "DkRedBgBrush",
            AlertSeverity.High => "DkOrangeBgBrush",
            AlertSeverity.Medium => "DkYellowBgBrush",
            _ => "DkGrayBgBrush"
        });

        public static string StateMarker(AlertState state) => state switch
        {
            AlertState.Unprocessed => "▲",
            AlertState.Acknowledged => "○",
            AlertState.InProgress => "▲",
            AlertState.Resolved => "●",
            _ => "■"
        };

        public static Microsoft.UI.Xaml.Media.Brush StateBrush(AlertState state) => Res(state switch
        {
            AlertState.Unprocessed => "DkRedBrush",
            AlertState.Acknowledged => "DkBlueLightBrush",
            AlertState.InProgress => "DkYellowBrush",
            AlertState.Resolved => "DkGreenBrush",
            _ => "DkGrayBrush"
        });

        public static Microsoft.UI.Xaml.Media.Brush StateBgBrush(AlertState state) => Res(state switch
        {
            AlertState.Unprocessed => "DkRedBgBrush",
            AlertState.Acknowledged => "DkBlueBgBrush",
            AlertState.InProgress => "DkYellowBgBrush",
            AlertState.Resolved => "DkGreenBgBrush",
            _ => "DkGrayBgBrush"
        });

        /// <summary>Count brush: severity color only while count &gt; 0.</summary>
        public static Microsoft.UI.Xaml.Media.Brush CountBrush(int count, string activeKey)
            => Res(count > 0 ? activeKey : "DkTextMutedBrush");

        public static Microsoft.UI.Xaml.Media.Brush CriticalCountBrush(int count) => CountBrush(count, "DkRedBrush");
        public static Microsoft.UI.Xaml.Media.Brush WarningCountBrush(int count) => CountBrush(count, "DkYellowBrush");

        private void CurrentViewButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySegmentState(CurrentViewButton, true);
            ApplySegmentState(HistoryViewButton, false);
            ViewModel.SelectedPeriod = "Hôm nay";
        }

        private void HistoryViewButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySegmentState(HistoryViewButton, true);
            ApplySegmentState(CurrentViewButton, false);
            ViewModel.SelectedPeriod = "Tất cả";
        }

        private async void ExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Xuất CSV",
                Content = $"Xuất {ViewModel.FilteredCount} cảnh báo ra file CSV...\n(Tính năng đang phát triển)",
                CloseButtonText = "Đóng",
                XamlRoot = this.XamlRoot,
                RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme
            };
            await dialog.ShowAsync();
        }

        // ── Filter buttons ──────────────────────────────────────────────

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshAlertsCommand.Execute(null);
        }

        private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearFiltersCommand.Execute(null);
        }

        // ── Table row action buttons ────────────────────────────────────

        private void AcknowledgeRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AlertItemViewModel alert)
                ViewModel.AcknowledgeAlertCommand.Execute(alert);
        }

        private async void DetailRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AlertItemViewModel alertVm)
            {
                if (!string.IsNullOrEmpty(alertVm.CameraId))
                {
                    // Has camera — open AlertVideoDialog
                    var alert = new Alert
                    {
                        Title = alertVm.Title,
                        Description = alertVm.Description,
                        Severity = alertVm.Severity,
                        CameraId = alertVm.CameraId,
                        NodeId = alertVm.NodeId,
                        NodeName = alertVm.NodeName,
                        LineName = alertVm.LineName,
                        CreatedAt = alertVm.CreatedAt,
                        SensorValue = alertVm.SensorValue,
                        SensorUnit = alertVm.SensorUnit,
                        Threshold = alertVm.Threshold
                    };
                    var videoDialog = new AlertVideoDialog(alert) { XamlRoot = this.XamlRoot };
                    await videoDialog.ShowAsync();
                    if (videoDialog.WasAcknowledged)
                        ViewModel.AcknowledgeAlertCommand.Execute(alertVm);
                }
                else
                {
                    // Text-only detail
                    var dialog = new ContentDialog
                    {
                        Title = alertVm.Title,
                        Content = BuildDetailContent(alertVm),
                        CloseButtonText = "Đóng",
                        PrimaryButtonText = alertVm.CanAcknowledge ? "Xác nhận" : null,
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme
                    };
                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                        ViewModel.AcknowledgeAlertCommand.Execute(alertVm);
                }
            }
        }

        private static string BuildDetailContent(AlertItemViewModel a)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Mức độ:     {a.SeverityText}");
            sb.AppendLine($"Loại:       {a.CategoryText}");
            sb.AppendLine($"Tuyến:      {a.LineName}");
            sb.AppendLine($"Nút:        {a.NodeName}");
            sb.AppendLine($"Thời gian:  {a.CreatedAtFormatted}");
            sb.AppendLine($"Trạng thái: {a.StateText}");
            if (!string.IsNullOrEmpty(a.SensorId))
                sb.AppendLine($"Cảm biến:   {a.SensorName} = {a.SensorValue:F1} {a.SensorUnit} (ngưỡng: {a.Threshold:F1})");
            sb.AppendLine();
            sb.Append(a.Description);
            return sb.ToString();
        }
    }
}
