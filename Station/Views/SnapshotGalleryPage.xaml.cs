using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Station.ViewModels;
using System;

namespace Station.Views;

public sealed partial class SnapshotGalleryPage : Page
{
    private readonly SnapshotGalleryViewModel _viewModel = new();

    public SnapshotGalleryPage()
    {
        InitializeComponent();
        _ = InitAsync();
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        // Wait for cameras to load
        await System.Threading.Tasks.Task.Delay(500);
        foreach (var id in _viewModel.CameraIds)
            CameraCombo.Items.Add(id);

        if (CameraCombo.Items.Count > 0)
            CameraCombo.SelectedIndex = 0;

        FromDatePicker.Date = DateTime.Today.AddDays(-1);
        ToDatePicker.Date = DateTime.Today.AddDays(1);
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SelectedCameraId = CameraCombo.SelectedItem?.ToString() ?? string.Empty;
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        LoadingRing.IsActive = true;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        SnapshotGrid.Items.Clear();

        _viewModel.FromDate = FromDatePicker.Date.DateTime;
        _viewModel.ToDate = ToDatePicker.Date.DateTime;

        await _viewModel.LoadSnapshotsCommand.ExecuteAsync(null);

        foreach (var snap in _viewModel.Snapshots)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(112) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var img = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            try { img.Source = new BitmapImage(new Uri(snap.ThumbnailUrl)); } catch { }
            Grid.SetRow(img, 0);

            var txt = new TextBlock
            {
                Text = snap.Timestamp.ToString("HH:mm:ss dd/MM"),
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Station.Services.ThemeBrushes.Get("DkTextSecondaryBrush"),
                Padding = new Thickness(4, 4, 4, 2),
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(txt, 1);

            grid.Children.Add(img);
            grid.Children.Add(txt);
            grid.Width = 200;
            grid.Margin = new Thickness(4);

            SnapshotGrid.Items.Add(grid);
        }

        TotalText.Text = $"Tổng: {_viewModel.TotalSnapshots} ảnh";
        EmptyStatePanel.Visibility = SnapshotGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateTitle.Text = "Không có ảnh nào trong khoảng thời gian đã chọn";
        EmptyStateHint.Text = "Thử đổi camera hoặc mở rộng khoảng thời gian";
        LoadingRing.IsActive = false;
    }

    private void SnapshotGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.PreviousPageCommand.Execute(null);
        _ = RefreshGridAsync();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NextPageCommand.Execute(null);
        _ = RefreshGridAsync();
    }

    private async System.Threading.Tasks.Task RefreshGridAsync()
    {
        SearchButton_Click(this, new RoutedEventArgs());
        await System.Threading.Tasks.Task.CompletedTask;
    }
}
