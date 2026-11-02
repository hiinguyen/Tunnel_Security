using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Station.ViewModels;
using System;

namespace Station.Views
{
    public sealed partial class DevicesPage : Page
    {
        public DevicesViewModel ViewModel { get; }

        public DevicesPage()
        {
            this.InitializeComponent();
            ViewModel = new DevicesViewModel();
            this.DataContext = ViewModel;
            SetActiveTab(isJoinRequestsTab: false);
            UpdateStatusButtonStyles(StatusAllBtn);
        }

        /// <summary>
        /// Amber only while there is something to act on; a zero count stays muted so the
        /// severity color keeps its meaning.
        /// </summary>
        public static Brush FaultCountBrush(int count)
            => (Brush)Station.Services.ThemeBrushes.Get(count > 0 ? "DkYellowBrush" : "DkTextMutedBrush");

        private static void ApplySegmentState(Button button, bool isActive)
        {
            if (isActive)
            {
                button.Background = (Brush)Station.Services.ThemeBrushes.Get("DkBlueBgBrush");
                button.Foreground = (Brush)Station.Services.ThemeBrushes.Get("DkBlueLightBrush");
                button.BorderBrush = (Brush)Station.Services.ThemeBrushes.Get("DkBlueBorderBrush");
                button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.Foreground = (Brush)Station.Services.ThemeBrushes.Get("DkTextSecondaryBrush");
                button.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        private void DevicesTabBtn_Click(object sender, RoutedEventArgs e)
            => SetActiveTab(isJoinRequestsTab: false);

        private void JoinRequestsTabBtn_Click(object sender, RoutedEventArgs e)
            => SetActiveTab(isJoinRequestsTab: true);

        private void SetActiveTab(bool isJoinRequestsTab)
        {
            ApplySegmentState(DevicesTabBtn, !isJoinRequestsTab);
            ApplySegmentState(JoinRequestsTabBtn, isJoinRequestsTab);

            if (isJoinRequestsTab)
            {
                StatsPanel.Visibility = Visibility.Collapsed;
                DeviceFilterPanel.Visibility = Visibility.Collapsed;
                DeviceTabContent.Visibility = Visibility.Collapsed;
                JoinTabContent.Visibility = Visibility.Visible;
            }
            else
            {
                StatsPanel.Visibility = Visibility.Visible;
                DeviceFilterPanel.Visibility = Visibility.Visible;
                DeviceTabContent.Visibility = Visibility.Visible;
                JoinTabContent.Visibility = Visibility.Collapsed;
            }
        }

        private async void AddDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Station.Dialogs.AddNodeDialog(ViewModel);
                dialog.XamlRoot = this.XamlRoot;
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Lỗi",
                    Content = $"Không thể mở dialog thêm thiết bị: {ex.Message}",
                    CloseButtonText = "Đóng",
                    XamlRoot = this.XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }

        private void StatusFilter_Tapped(object sender, RoutedEventArgs e)
        {
            if (sender is not Button clicked) return;

            string status = clicked.Tag?.ToString() ?? "Tất cả";
            
            // Update all buttons' styles based on which one was clicked
            UpdateStatusButtonStyles(clicked);

            // Apply filter
            if (status == "Tất cả")
                ViewModel.SelectedStatus = "Tất cả trạng thái";
            else if (status == "Hoạt động")
                ViewModel.SelectedStatus = "Hoạt động";
            else if (status == "Ngoại tuyến")
                ViewModel.SelectedStatus = "Ngoại tuyến";
        }

        private void UpdateStatusButtonStyles(Button activeBtn)
        {
            var allBtns = new[] { StatusAllBtn, StatusOnlineBtn, StatusOfflineBtn };
            
            foreach (var btn in allBtns)
                ApplySegmentState(btn, btn == activeBtn);
        }

        private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Filter implementation based on ComboBox selection
            // This would update the ViewModel's device type filter
        }

        private async void DeviceCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border border && border.Tag is NodeItemViewModel node)
            {
                var dialog = new Station.Dialogs.NodeDetailDialog(node);
                dialog.XamlRoot = this.XamlRoot;
                await dialog.ShowAsync();
            }
        }

        private async void ViewDetails_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NodeItemViewModel node)
            {
                var dialog = new Station.Dialogs.NodeDetailDialog(node);
                dialog.XamlRoot = this.XamlRoot;
                await dialog.ShowAsync();
            }
        }

        private void TableRow_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
            {
                // Use a slightly darker surface for hover effect
                b.Background = (SolidColorBrush)Station.Services.ThemeBrushes.Get("DkBorderSubtleBrush");
            }
        }

        private void TableRow_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
            {
                b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NodeItemViewModel node)
            {
                var dialog = new Station.Dialogs.EditNodeDialog(node, ViewModel);
                dialog.XamlRoot = this.XamlRoot;
                await dialog.ShowAsync();
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NodeItemViewModel node)
            {
                var confirm = new ContentDialog
                {
                    Title = "Xóa thiết bị",
                    Content = $"Xác nhận xóa node \"{node.NodeName}\"?\nTất cả {node.Sensors.Count} cảm biến sẽ bị xóa khỏi danh sách.",
                    PrimaryButtonText = "Xóa",
                    CloseButtonText = "Hủy",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme
                };
                if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                    ViewModel.DeleteNode(node);
            }
        }
    }
}
