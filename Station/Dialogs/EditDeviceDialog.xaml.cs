using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Station.ViewModels;
using Windows.UI;

namespace Station.Dialogs;

/// <summary>
/// Content Dialog for editing device information
/// </summary>
public sealed partial class EditDeviceDialog : ContentDialog
{
    private readonly DeviceItemViewModel _device;

    public EditDeviceDialog(DeviceItemViewModel device)
    {
        this.InitializeComponent();
        RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
        _device = device;
        LoadDeviceData();

        // Handle save button click
        this.PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void HeaderClose_Click(object sender, RoutedEventArgs e) => Hide();

    private void LoadDeviceData()
    {
        // Load current device data into form fields
        DeviceNameTextBox.Text = _device.Name;
        DeviceIdTextBox.Text = _device.DeviceId;
        TypeDisplayTextBox.Text = _device.TypeDisplay;
        LocationTextBox.Text = _device.Location;
        IpAddressTextBox.Text = _device.IpAddress;
        ManufacturerTextBox.Text = _device.Manufacturer;
        FirmwareVersionTextBox.Text = _device.FirmwareVersion;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(DeviceNameTextBox.Text))
        {
            args.Cancel = true;
            _ = ShowValidationErrorAsync("Vui lòng nhập tên thiết bị");
            return;
        }

        if (string.IsNullOrWhiteSpace(IpAddressTextBox.Text))
        {
            args.Cancel = true;
            _ = ShowValidationErrorAsync("Vui lòng nhập địa chỉ IP");
            return;
        }

        // Update device properties
        _device.Name = DeviceNameTextBox.Text.Trim();
        _device.TypeDisplay = TypeDisplayTextBox.Text.Trim();
        _device.Location = LocationTextBox.Text.Trim();
        _device.IpAddress = IpAddressTextBox.Text.Trim();
        _device.Manufacturer = ManufacturerTextBox.Text.Trim();
        _device.FirmwareVersion = FirmwareVersionTextBox.Text.Trim();

        System.Diagnostics.Debug.WriteLine($"Device updated: {_device.Name}");
    }

    private async System.Threading.Tasks.Task ShowValidationErrorAsync(string message)
    {
        var errorDialog = new ContentDialog
        {
            Title = "Lỗi xác thực",
            Content = message,
            CloseButtonText = "Đóng",
            XamlRoot = this.XamlRoot,
            RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme,
            Background = (Brush)Station.Services.ThemeBrushes.Get("DkSurfaceBrush"),
            Foreground = (Brush)Station.Services.ThemeBrushes.Get("DkTextPrimaryBrush"),
            BorderBrush = (Brush)Station.Services.ThemeBrushes.Get("DkBorderBrush")
        };

        await errorDialog.ShowAsync();
    }
}
