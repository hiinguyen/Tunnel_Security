using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace Station.Dialogs
{
    public sealed partial class SensorConfigDialog : ContentDialog
    {
        private string _sensorId;
        private string _sensorName;
        private int _sensorType;
        private double _currentValue;
        private string _unit;

        public SensorConfigDialog(string sensorId, string sensorName, int sensorType, double currentValue, string unit, string icon, SolidColorBrush iconBackground)
        {
            this.InitializeComponent();
            RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;

            _sensorId = sensorId;
            _sensorName = sensorName;
            _sensorType = sensorType;
            _currentValue = currentValue;
            _unit = unit;

            // Set dialog title
            Title = $"Cấu hình {sensorName}";

            // Set sensor info
            SensorNameText.Text = sensorName;
            SensorTypeText.Text = GetSensorTypeName(sensorType);
            SensorIconText.Text = icon;
            SensorIconBorder.Background = iconBackground;

            // Set current value
            ValueRun.Text = currentValue.ToString("0.0");
            UnitRun.Text = unit;

            // Load current configuration
            LoadConfiguration();
        }

        private void HeaderClose_Click(object sender, RoutedEventArgs e) => Hide();

        private string GetSensorTypeName(int type)
        {
            return type switch
            {
                0 => "RADAR",
                1 => "TEMPERATURE",
                2 => "HUMIDITY",
                3 => "VIBRATION",
                4 => "WATERLEVEL",
                5 => "SMOKE",
                _ => "UNKNOWN"
            };
        }

        private void LoadConfiguration()
        {
            // Load from local storage or API
            // For now, use default values
            SensorNameInput.Text = _sensorName;
            EnabledToggle.IsOn = true;
            SamplingRateSlider.Value = 5;
            
            // Set thresholds based on sensor type and unit
            SetDefaultThresholds();
        }

        private void SetDefaultThresholds()
        {
            // Set appropriate ranges based on sensor type
            switch (_sensorType)
            {
                case 0: // RADAR - mm
                    WarningThresholdSlider.Maximum = 10;
                    CriticalThresholdSlider.Maximum = 10;
                    WarningThresholdSlider.Value = 3;
                    CriticalThresholdSlider.Value = 7;
                    break;
                case 1: // TEMPERATURE - °C
                    WarningThresholdSlider.Maximum = 100;
                    CriticalThresholdSlider.Maximum = 100;
                    WarningThresholdSlider.Value = 35;
                    CriticalThresholdSlider.Value = 45;
                    break;
                case 2: // HUMIDITY - %
                    WarningThresholdSlider.Maximum = 100;
                    CriticalThresholdSlider.Maximum = 100;
                    WarningThresholdSlider.Value = 70;
                    CriticalThresholdSlider.Value = 85;
                    break;
                case 3: // VIBRATION - mm/s
                    WarningThresholdSlider.Maximum = 20;
                    CriticalThresholdSlider.Maximum = 20;
                    WarningThresholdSlider.Value = 5;
                    CriticalThresholdSlider.Value = 10;
                    break;
                case 4: // WATERLEVEL - cm
                    WarningThresholdSlider.Maximum = 200;
                    CriticalThresholdSlider.Maximum = 200;
                    WarningThresholdSlider.Value = 100;
                    CriticalThresholdSlider.Value = 150;
                    break;
                case 5: // SMOKE
                    WarningThresholdSlider.Maximum = 100;
                    CriticalThresholdSlider.Maximum = 100;
                    WarningThresholdSlider.Value = 30;
                    CriticalThresholdSlider.Value = 70;
                    break;
                default:
                    WarningThresholdSlider.Maximum = 100;
                    CriticalThresholdSlider.Maximum = 100;
                    WarningThresholdSlider.Value = 30;
                    CriticalThresholdSlider.Value = 70;
                    break;
            }

            UpdateThresholdDisplays();
        }

        private void SamplingRateSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (SamplingRateValue != null)
            {
                SamplingRateValue.Text = $"{(int)e.NewValue} giây";
            }
        }

        private void WarningThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (WarningThresholdInput != null)
            {
                WarningThresholdInput.Text = e.NewValue.ToString("0.0");
                UpdateWarningDisplay(e.NewValue);
            }
        }

        private void CriticalThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (CriticalThresholdInput != null)
            {
                CriticalThresholdInput.Text = e.NewValue.ToString("0.0");
                UpdateCriticalDisplay(e.NewValue);
            }
        }

        private void WarningThresholdInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(WarningThresholdInput.Text, out double value))
            {
                if (value >= WarningThresholdSlider.Minimum && value <= WarningThresholdSlider.Maximum)
                {
                    WarningThresholdSlider.ValueChanged -= WarningThresholdSlider_ValueChanged;
                    WarningThresholdSlider.Value = value;
                    WarningThresholdSlider.ValueChanged += WarningThresholdSlider_ValueChanged;
                    UpdateWarningDisplay(value);
                }
            }
        }

        private void CriticalThresholdInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(CriticalThresholdInput.Text, out double value))
            {
                if (value >= CriticalThresholdSlider.Minimum && value <= CriticalThresholdSlider.Maximum)
                {
                    CriticalThresholdSlider.ValueChanged -= CriticalThresholdSlider_ValueChanged;
                    CriticalThresholdSlider.Value = value;
                    CriticalThresholdSlider.ValueChanged += CriticalThresholdSlider_ValueChanged;
                    UpdateCriticalDisplay(value);
                }
            }
        }

        private void UpdateWarningDisplay(double value)
        {
            if (WarningValueText != null)
            {
                WarningValueText.Text = $"{value:0.0} {_unit}";
            }
        }

        private void UpdateCriticalDisplay(double value)
        {
            if (CriticalValueText != null)
            {
                CriticalValueText.Text = $"{value:0.0} {_unit}";
            }
        }

        private void UpdateThresholdDisplays()
        {
            UpdateWarningDisplay(WarningThresholdSlider.Value);
            UpdateCriticalDisplay(CriticalThresholdSlider.Value);
        }

        private void SmoothingSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (SmoothingValue != null)
            {
                SmoothingValue.Text = $"{(int)e.NewValue} mẫu";
            }
        }

        private void CalibrationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (CalibrationInput != null)
            {
                CalibrationInput.Text = e.NewValue.ToString("0.0");
            }
        }

        private void CalibrationInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(CalibrationInput.Text, out double value))
            {
                if (value >= CalibrationSlider.Minimum && value <= CalibrationSlider.Maximum)
                {
                    CalibrationSlider.ValueChanged -= CalibrationSlider_ValueChanged;
                    CalibrationSlider.Value = value;
                    CalibrationSlider.ValueChanged += CalibrationSlider_ValueChanged;
                }
            }
        }

        private async void SaveButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Validate thresholds
            if (CriticalThresholdSlider.Value <= WarningThresholdSlider.Value)
            {
                args.Cancel = true;
                ConfigInfoBar.Severity = InfoBarSeverity.Error;
                ConfigInfoBar.Message = "Ngưỡng nghiêm trọng phải lớn hơn ngưỡng cảnh báo!";
                ConfigInfoBar.IsOpen = true;
                return;
            }

            // Save configuration
            var config = new SensorConfiguration
            {
                SensorId = _sensorId,
                Name = SensorNameInput.Text,
                IsEnabled = EnabledToggle.IsOn,
                SamplingRate = (int)SamplingRateSlider.Value,
                WarningThreshold = WarningThresholdSlider.Value,
                CriticalThreshold = CriticalThresholdSlider.Value,
                AlertEnabled = AlertEnabledToggle.IsOn,
                DataSmoothing = (int)SmoothingSlider.Value,
                CalibrationOffset = CalibrationSlider.Value,
                AutoReset = AutoResetToggle.IsOn
            };

            // TODO: Send to API
            System.Diagnostics.Debug.WriteLine($"Saving sensor config: {_sensorId}");
            System.Diagnostics.Debug.WriteLine($"  Name: {config.Name}");
            System.Diagnostics.Debug.WriteLine($"  Enabled: {config.IsEnabled}");
            System.Diagnostics.Debug.WriteLine($"  Sampling Rate: {config.SamplingRate}s");
            System.Diagnostics.Debug.WriteLine($"  Warning: {config.WarningThreshold:0.0} {_unit}");
            System.Diagnostics.Debug.WriteLine($"  Critical: {config.CriticalThreshold:0.0} {_unit}");
            System.Diagnostics.Debug.WriteLine($"  Smoothing: {config.DataSmoothing}");
            System.Diagnostics.Debug.WriteLine($"  Calibration: {config.CalibrationOffset:0.0}");

            // Show success message
            ConfigInfoBar.Severity = InfoBarSeverity.Success;
            ConfigInfoBar.Message = "Cấu hình đã được lưu thành công!";
            ConfigInfoBar.IsOpen = true;

            // Wait a bit before closing
            await System.Threading.Tasks.Task.Delay(1000);
        }
    }

    public class SensorConfiguration
    {
        public string SensorId { get; set; }
        public string Name { get; set; }
        public bool IsEnabled { get; set; }
        public int SamplingRate { get; set; }
        public double WarningThreshold { get; set; }
        public double CriticalThreshold { get; set; }
        public bool AlertEnabled { get; set; }
        public int DataSmoothing { get; set; }
        public double CalibrationOffset { get; set; }
        public bool AutoReset { get; set; }
    }
}
