using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Station.ViewModels;
using System;
using Windows.UI;

namespace Station.Dialogs
{
    public sealed partial class NodeDetailDialog : ContentDialog
    {
        private readonly NodeItemViewModel _node;
        private int _activeTab = 0;

        public NodeDetailDialog(NodeItemViewModel node)
        {
            this.InitializeComponent();
            RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
            _node = node;
            PopulateData();
        }

        private void PopulateData()
        {
            // Header
            DialogNodeName.Text = _node.NodeName;
            DialogNodeLocation.Text = _node.Location;

            DialogStatusBadge.Background = _node.StatusBackgroundColor;
            DialogStatusBadge.BorderBrush = _node.StatusColor;
            DialogStatusBadge.BorderThickness = new Thickness(1);
            DialogStatusText.Text = $"{_node.StatusMarker} {_node.StatusText.ToUpper()}";
            DialogStatusText.Foreground = _node.StatusColor;

            // Detail list
            DetailStatusText.Text = $"{_node.StatusMarker} {_node.StatusText}";
            DetailStatusText.Foreground = _node.StatusColor;
            DetailLineText.Text = _node.LineName;
            DetailSensorCountText.Text = _node.SensorCountText;

            // Map
            MapLocationText.Text = _node.Location;

            // Sensor grid
            SensorReadingsRepeater.ItemsSource = _node.Sensors;

            // Tab 3 camera info
            var cam = FindCamera();
            CamTimestampText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            CamIdText.Text = cam ?? $"CAM_{_node.NodeName.Replace("-", "_").Replace(" ", "_")}";
            CamIdDetail.Text = cam ?? "-";
            CamStatusDetail.Text = cam != null ? "Trực tuyến" : "Không kết nối";
            if (cam == null)
                CamStatusDetail.Foreground = (Brush)Station.Services.ThemeBrushes.Get("DkGrayBrush");
        }

        private string? FindCamera()
        {
            foreach (var s in _node.Sensors)
                if (s.SensorType == "Camera")
                    return s.SensorId;
            return null;
        }

        // --- Tab switching
        private void Tab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not Border border) return;
            if (!int.TryParse(border.Tag?.ToString(), out int idx)) return;
            SetActiveTab(idx);
        }

        private void SetActiveTab(int idx)
        {
            _activeTab = idx;

            // Reset all tabs
            SetTabStyle(Tab1Border, Tab1Text, false);
            SetTabStyle(Tab2Border, Tab2Text, false);
            SetTabStyle(Tab3Border, Tab3Text, false);

            // Activate selected
            var (border, text) = idx switch
            {
                1 => (Tab2Border, Tab2Text),
                2 => (Tab3Border, Tab3Text),
                _ => (Tab1Border, Tab1Text)
            };
            SetTabStyle(border, text, true);

            // Show / hide panels
            Tab1Panel.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            Tab2Panel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            Tab3Panel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetTabStyle(Border border, TextBlock text, bool active)
        {
            var blueBrush = (SolidColorBrush)Station.Services.ThemeBrushes.Get("DkBlueLightBrush");
            var mutedBrush = (SolidColorBrush)Station.Services.ThemeBrushes.Get("DkTextMutedBrush");

            border.BorderBrush = active ? blueBrush : new SolidColorBrush(Colors.Transparent);
            text.Foreground = active ? blueBrush : mutedBrush;
        }

        // --- Button handlers ─────────────────────────────────────────────
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private async void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new ContentDialog
            {
                Title = "Khởi động lại thiết bị",
                Content = $"Xác nhận khởi động lại nút \"{_node.NodeName}\"?\nThiết bị sẽ ngắt kết nối trong vài giây.",
                PrimaryButtonText = "Khởi động lại",
                CloseButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme
            };
            var result = await confirm.ShowAsync();
            if (result == ContentDialogResult.Primary)
                System.Diagnostics.Debug.WriteLine($"[NodeDetailDialog] Restart: {_node.NodeName}");
        }
    }
}
