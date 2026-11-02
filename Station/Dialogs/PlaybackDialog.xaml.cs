using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Station.ViewModels;
using System;
using Windows.Storage.Pickers;
using Windows.Storage;

namespace Station.Dialogs
{
    public sealed partial class PlaybackDialog : ContentDialog
    {
        private DeviceItemViewModel _device;
        private bool _isPlaying = false;
        private double _playbackSpeed = 1.0;
        private DispatcherTimer _playbackTimer;
        private DateTimeOffset _currentDate = DateTimeOffset.Now;

        public DateTimeOffset CurrentDate
        {
            get => _currentDate;
            set => _currentDate = value;
        }

        public PlaybackDialog()
        {
            this.InitializeComponent();
            RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
            InitializePlaybackTimer();
            LoadInitialData();
        }

        private void HeaderClose_Click(object sender, RoutedEventArgs e) => Hide();

        public PlaybackDialog(DeviceItemViewModel device) : this()
        {
            _device = device;
            LoadDeviceInfo();
        }

        public PlaybackDialog(string cameraId) : this()
        {
            // Create a temporary device object for display
            _device = new DeviceItemViewModel
            {
                DeviceId = cameraId,
                Name = $"Camera {cameraId}"
            };
            LoadDeviceInfo();
        }

        private void LoadInitialData()
        {
            // Set current date
            CurrentDate = DateTimeOffset.Now;
            PlaybackDatePicker.Date = CurrentDate;
        }

        private void LoadDeviceInfo()
        {
            if (_device != null)
            {
                DeviceNameOverlay.Text = $"Playback - {_device.Name}";
            }
        }

        private void InitializePlaybackTimer()
        {
            _playbackTimer = new DispatcherTimer();
            _playbackTimer.Interval = TimeSpan.FromSeconds(1);
            _playbackTimer.Tick += PlaybackTimer_Tick;
        }

        private void PlaybackTimer_Tick(object sender, object e)
        {
            if (_isPlaying && ProgressSlider.Value < ProgressSlider.Maximum)
            {
                // Advance playback based on speed
                ProgressSlider.Value += _playbackSpeed;
                UpdateTimeDisplay();
            }
            else if (ProgressSlider.Value >= ProgressSlider.Maximum)
            {
                // End of playback
                StopPlayback();
            }
        }

        private void UpdateTimeDisplay()
        {
            var currentSeconds = (int)ProgressSlider.Value;
            var totalSeconds = (int)ProgressSlider.Maximum;

            var currentTime = TimeSpan.FromSeconds(currentSeconds);
            var totalTime = TimeSpan.FromSeconds(totalSeconds);

            TimeDisplay.Text = $"{currentTime:hh\\:mm\\:ss} / {totalTime:hh\\:mm\\:ss}";
        }

        // Play/Pause Button
        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            _isPlaying = !_isPlaying;

            if (_isPlaying)
            {
                PlayPauseIcon.Glyph = "\uE769"; // Pause icon
                _playbackTimer.Start();
                ShowNotification("Đang phát video", InfoBarSeverity.Informational);
            }
            else
            {
                PlayPauseIcon.Glyph = "\uE768"; // Play icon
                _playbackTimer.Stop();
                ShowNotification("Tạm dừng", InfoBarSeverity.Informational);
            }
        }

        // Stop Button
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
        }

        private void StopPlayback()
        {
            _isPlaying = false;
            _playbackTimer.Stop();
            ProgressSlider.Value = 0;
            PlayPauseIcon.Glyph = "\uE768"; // Play icon
            UpdateTimeDisplay();
            ShowNotification("Đã dừng", InfoBarSeverity.Informational);
        }

        // Rewind Button
        private void RewindButton_Click(object sender, RoutedEventArgs e)
        {
            ProgressSlider.Value = Math.Max(0, ProgressSlider.Value - 10);
            UpdateTimeDisplay();
            ShowNotification("Tua lùi 10 giây", InfoBarSeverity.Informational);
        }

        // Fast Forward Button
        private void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
            ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, ProgressSlider.Value + 10);
            UpdateTimeDisplay();
            ShowNotification("Tua tới 10 giây", InfoBarSeverity.Informational);
        }

        // Progress Slider
        private void ProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            UpdateTimeDisplay();
        }

        // Speed Options
        private void SpeedOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag != null)
            {
                _playbackSpeed = Convert.ToDouble(item.Tag);
                SpeedText.Text = $"{_playbackSpeed}x";
                ShowNotification($"Tốc độ phát: {_playbackSpeed}x", InfoBarSeverity.Informational);
            }
        }

        // Snapshot Button
        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            ShowNotification("Đã chụp ảnh thành công", InfoBarSeverity.Success);
            // TODO: Implement actual snapshot functionality
        }

        // Fullscreen Button
        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            ShowNotification("Chế độ toàn màn hình", InfoBarSeverity.Informational);
            // TODO: Implement fullscreen mode
        }

        // Date Picker
        private void PlaybackDatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (args.NewDate.HasValue)
            {
                CurrentDate = args.NewDate.Value;
                StopPlayback();
                ShowNotification($"Đã chọn ngày: {CurrentDate:dd/MM/yyyy}", InfoBarSeverity.Informational);
                // TODO: Load video for selected date
            }
        }

        // Download Button
        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowNotification("Đang chuẩn bị tải xuống...", InfoBarSeverity.Informational);

                // Simulate download process
                await System.Threading.Tasks.Task.Delay(2000);

                ShowNotification("Đã tải xuống thành công", InfoBarSeverity.Success);
                // TODO: Implement actual download functionality
            }
            catch (Exception ex)
            {
                ShowNotification($"Lỗi tải xuống: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private async void ShowNotification(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
        {
            NotificationBar.Message = message;
            NotificationBar.Severity = severity;
            NotificationBar.IsOpen = true;

            // Auto-hide after 3 seconds
            await System.Threading.Tasks.Task.Delay(3000);
            NotificationBar.IsOpen = false;
        }
    }

    // Value Converter for Volume Display
    public class DoubleToIntConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double doubleValue)
            {
                return ((int)doubleValue).ToString();
            }
            return "0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
