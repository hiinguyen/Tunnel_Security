using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Station.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Station.Dialogs
{
    public sealed partial class DeviceControlDialog : ContentDialog
    {
        private DeviceItemViewModel _device;
        private string _nodeId;
        private string _deviceType;
        private const string BackendBaseUrl = "http://localhost:5280";
        private const string StationId = "ST01";

        public DeviceControlDialog()
        {
            this.InitializeComponent();
            RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
        }

        public DeviceControlDialog(DeviceItemViewModel device) : this()
        {
            _device = device;
            _deviceType = device.Type ?? "Sensor";
            _nodeId = device.DeviceId;
            LoadDeviceInfo();
            _ = LoadSensorsDataAsync();
        }

        public DeviceControlDialog(string nodeId, string deviceType) : this()
        {
            _nodeId = nodeId;
            _deviceType = deviceType;
            LoadNodeInfo(nodeId, deviceType);
            _ = LoadSensorsDataAsync();
        }

        private async void LoadNodeInfo(string nodeId, string deviceType)
        {
            DeviceNameText.Text = $"Node {nodeId}";
            DeviceIdText.Text = nodeId;
            await LoadNodeDataFromApi(nodeId);
        }

        private async Task LoadNodeDataFromApi(string nodeId)
        {
            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync($"{BackendBaseUrl}/api/stations/{StationId}/nodes");
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var geojson = JsonDocument.Parse(json);
                    
                    foreach (var feature in geojson.RootElement.GetProperty("features").EnumerateArray())
                    {
                        var props = feature.GetProperty("properties");
                        var id = props.GetProperty("id").GetString();
                        
                        if (id == nodeId)
                        {
                            DeviceNameText.Text = props.GetProperty("name").GetString();
                            BatteryText.Text = $"{props.GetProperty("batteryLevel").GetInt32()}%";
                            RSSIText.Text = $"{props.GetProperty("rssi").GetInt32()} dBm";
                            
                            var sensorCount = props.GetProperty("sensorCount").GetInt32();
                            SensorCountText.Text = sensorCount.ToString();
                            
                            var lastOnline = props.GetProperty("lastOnline").GetDateTime();
                            LastOnlineText.Text = FormatDateTime(lastOnline);
                            
                            var status = props.GetProperty("status").GetInt32();
                            UpdateStatusBadge(status);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading node data: {ex.Message}");
            }
        }

        private async Task LoadSensorsDataAsync()
        {
            try
            {
                SensorHeaderText.Text = "Đang tải...";

                if (string.IsNullOrEmpty(_nodeId))
                {
                    SensorHeaderText.Text = "Không có thông tin node";
                    return;
                }
                
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                
                var url = $"{BackendBaseUrl}/api/stations/{StationId}/nodes/{_nodeId}/sensors";
                System.Diagnostics.Debug.WriteLine($"Loading sensors from: {url}");
                
                var response = await client.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"Sensors JSON: {json.Substring(0, Math.Min(200, json.Length))}...");
                    
                    // API trả về object {"value": [...], "Count": 5}
                    List<SensorData> sensors;
                    try
                    {
                        var wrapper = JsonSerializer.Deserialize<SensorWrapper>(json, new JsonSerializerOptions 
                        { 
                            PropertyNameCaseInsensitive = true 
                        });
                        sensors = wrapper?.Value ?? new List<SensorData>();
                    }
                    catch
                    {
                        // Fallback: thử parse trực tiếp như array
                        sensors = JsonSerializer.Deserialize<List<SensorData>>(json, new JsonSerializerOptions 
                        { 
                            PropertyNameCaseInsensitive = true 
                        }) ?? new List<SensorData>();
                    }

                    if (sensors != null && sensors.Count > 0)
                    {
                        SensorHeaderText.Text = $"Cảm biến được gắn trong Node ({sensors.Count})";
                        SensorCountText.Text = sensors.Count.ToString();
                        
                        var sensorViewModels = sensors.Select(s => new SensorViewModel
                        {
                            Id = s.Id,
                            Name = GetSensorDisplayName(s.Type, s.Name),
                            Type = GetSensorTypeName(s.Type),
                            TypeCode = s.Type,
                            Value = s.CurrentValue?.ToString("0.0") ?? "N/A",
                            NumericValue = s.CurrentValue ?? 0,
                            Unit = s.Unit ?? "",
                            Icon = GetSensorIcon(s.Type, s.Name),
                            IconBackground = GetSensorIconBackground(s.Type),
                            ValueColor = GetValueColor(s),
                            StateText = GetStateText(s)
                        }).ToList();

                        SensorsItemsControl.ItemsSource = sensorViewModels;
                        SensorEmptyState.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        SensorHeaderText.Text = "Không có cảm biến";
                    }
                }
                else
                {
                    SensorHeaderText.Text = $"Lỗi API: {response.StatusCode}";
                    System.Diagnostics.Debug.WriteLine($"API Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                }
            }
            catch (HttpRequestException ex)
            {
                SensorHeaderText.Text = "Không kết nối được Backend";
                System.Diagnostics.Debug.WriteLine($"HTTP Error loading sensors: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                SensorHeaderText.Text = "Timeout khi tải dữ liệu";
            }
            catch (Exception ex)
            {
                SensorHeaderText.Text = $"Lỗi: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Error loading sensors: {ex.Message}");
            }
        }

        private void LoadDeviceInfo()
        {
            if (_device != null)
            {
                DeviceNameText.Text = _device.Name;
                DeviceIdText.Text = _device.DeviceId;
            }
        }

        private static Brush Res(string key) => (Brush)Station.Services.ThemeBrushes.Get(key);

        // DESIGN palette: green = online, yellow = warning, red = critical, gray = offline.
        // Every state carries a geometric marker so it never relies on color alone.
        private void UpdateStatusBadge(int status)
        {
            var (bgKey, fgKey, text) = status switch
            {
                0 => ("DkGreenBgBrush", "DkGreenBrush", "\u25CF Online"),
                1 => ("DkYellowBgBrush", "DkYellowBrush", "\u25B2 Warning"),
                2 => ("DkRedBgBrush", "DkRedBrush", "\u25B2 Critical"),
                3 => ("DkGrayBgBrush", "DkGrayBrush", "\u25CB Offline"),
                _ => ("DkGrayBgBrush", "DkGrayBrush", "\u25CB Unknown")
            };

            StatusBadge.Background = Res(bgKey);
            StatusBadge.BorderBrush = Res(fgKey);

            if (StatusBadge.Child is TextBlock statusText)
            {
                statusText.Text = text;
                statusText.Foreground = Res(fgKey);
            }
        }

        private string FormatDateTime(DateTime dt)
        {
            var diff = DateTime.Now - dt;
            if (diff.TotalMinutes < 1) return "Vừa xong";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} phút trước";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} giờ trước";
            return dt.ToString("dd/MM/yyyy HH:mm");
        }

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

        private string GetSensorDisplayName(int type, string name)
        {
            return type switch
            {
                0 => "Radar biển động",
                1 => "Nhiệt độ",
                2 => "Độ ẩm",
                3 => "Cảm biến rung",
                4 => "Mực nước",
                5 => "Cảm biến khói/lửa",
                _ => name ?? "Cảm biến"
            };
        }

        // Short instrument tag instead of an emoji pictogram. Same vocabulary as the map popup
        // (Assets/Map/map.html getSensorTag) and the SEN/CAM/RAD IDs used elsewhere in the app.
        private string GetSensorIcon(int type, string name)
        {
            return type switch
            {
                0 => "RAD",
                1 => "TEMP",
                2 => "HUM",
                3 => "VIB",
                4 => "LVL",
                5 => "FIRE",
                _ => "SEN"
            };
        }

        private SolidColorBrush GetSensorIconBackground(int type)
        {
            var color = type switch
            {
                0 => "#3B82F6",  // RADAR
                1 => "#EF4444",  // TEMPERATURE
                2 => "#06B6D4",  // HUMIDITY
                3 => "#8B5CF6",  // VIBRATION
                4 => "#0EA5E9",  // WATERLEVEL
                5 => "#F59E0B",  // SMOKE
                _ => "#6B7280"
            };

            return new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(40,
                    Convert.ToByte(color.Substring(1, 2), 16),
                    Convert.ToByte(color.Substring(3, 2), 16),
                    Convert.ToByte(color.Substring(5, 2), 16)));
        }

        private static SolidColorBrush ResBrush(string key) => (SolidColorBrush)Station.Services.ThemeBrushes.Get(key);

        private SolidColorBrush GetValueColor(SensorData sensor)
        {
            if (sensor.CriticalThreshold.HasValue && sensor.CurrentValue >= sensor.CriticalThreshold)
                return ResBrush("DkRedBrush");
            if (sensor.WarningThreshold.HasValue && sensor.CurrentValue >= sensor.WarningThreshold)
                return ResBrush("DkYellowBrush");
            return ResBrush("DkTextPrimaryBrush");
        }

        // Text state with geometric marker, paired with the value color.
        private string GetStateText(SensorData sensor)
        {
            if (sensor.CriticalThreshold.HasValue && sensor.CurrentValue >= sensor.CriticalThreshold)
                return "\u25B2 Nghiêm trọng";
            if (sensor.WarningThreshold.HasValue && sensor.CurrentValue >= sensor.WarningThreshold)
                return "\u25B2 Cảnh báo";
            return "\u25CF Bình thường";
        }

        private void HeaderClose_Click(object sender, RoutedEventArgs e) => Hide();

        private async void ViewCameraButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            var playbackDialog = new PlaybackDialog(_nodeId ?? "CAM-001");
            playbackDialog.XamlRoot = this.XamlRoot;
            await playbackDialog.ShowAsync();
        }

        private void PowerOnButton_Click(object sender, RoutedEventArgs e)
        {
            ShowNotification("Đã gửi lệnh bật Node", InfoBarSeverity.Success);
        }

        private void PowerOffButton_Click(object sender, RoutedEventArgs e)
        {
            ShowNotification("Đã gửi lệnh tắt Node", InfoBarSeverity.Warning);
        }

        private async void RebootButton_Click(object sender, RoutedEventArgs e)
        {
            RebootButton.IsEnabled = false;
            ShowNotification("Đang khởi động lại Node...", InfoBarSeverity.Informational);
            await Task.Delay(2000);
            ShowNotification("Node đã khởi động lại thành công", InfoBarSeverity.Success);
            RebootButton.IsEnabled = true;
        }

        private async void EditDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_device != null)
            {
                var editDialog = new EditDeviceDialog(_device);
                editDialog.XamlRoot = this.XamlRoot;
                await editDialog.ShowAsync();
            }
        }

        private async void SensorCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border && border.Tag is SensorViewModel sensor)
            {
                // Close current dialog first
                this.Hide();
                
                var configDialog = new SensorConfigDialog(
                    sensorId: sensor.Id,
                    sensorName: sensor.Name,
                    sensorType: sensor.TypeCode,
                    currentValue: sensor.NumericValue,
                    unit: sensor.Unit,
                    icon: sensor.Icon,
                    iconBackground: sensor.IconBackground
                );
                configDialog.XamlRoot = this.XamlRoot;
                var result = await configDialog.ShowAsync();
                
                // Reopen this dialog after sensor config is closed
                if (result == ContentDialogResult.None || result == ContentDialogResult.Secondary)
                {
                    await this.ShowAsync();
                }
            }
        }

        private void SensorCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
                border.Background = Res("DkHoverSurfaceBrush");
        }

        private void SensorCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
                border.Background = Res("DkInputBgBrush");
        }

        private async void ShowNotification(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
        {
            NotificationBar.Message = message;
            NotificationBar.Severity = severity;
            NotificationBar.IsOpen = true;
            await Task.Delay(3000);
            NotificationBar.IsOpen = false;
        }
    }

    public class SensorWrapper
    {
        public List<SensorData> Value { get; set; }
        public int Count { get; set; }
    }

    public class SensorData
    {
        public string Id { get; set; }
        public string NodeId { get; set; }
        public int Type { get; set; }  // Changed from string to int
        public string Name { get; set; }
        public string Unit { get; set; }
        public double? WarningThreshold { get; set; }
        public double? CriticalThreshold { get; set; }
        public double? CurrentValue { get; set; }
        public DateTime? LastReading { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class SensorViewModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public int TypeCode { get; set; }
        public string Value { get; set; }
        public double NumericValue { get; set; }
        public string Unit { get; set; }
        public string Icon { get; set; }
        public SolidColorBrush IconBackground { get; set; }
        public SolidColorBrush ValueColor { get; set; }
        public string StateText { get; set; }
    }
}
