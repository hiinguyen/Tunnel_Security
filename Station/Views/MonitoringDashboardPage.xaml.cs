using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Threading.Tasks;
using Station.Dialogs;
using Station.Services;
using Microsoft.UI.Xaml;
using Station.ViewModels;
using CommunityToolkit.WinUI.Controls.SettingsControlsRns;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Station.Config;
using Windows.Storage.Streams;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;

namespace Station.Views
{
    public sealed partial class MonitoringDashboardPage : Page
    {
        public MonitoringDashboardViewModel ViewModel { get; }

        private readonly ThemeService _themeService;
        private readonly CurrentUserSession _currentUserSession;
        private readonly UserApiService _userApiService = new();

        // Config sẽ được load từ .env file
        private string BackendBaseUrl => EnvironmentConfig.BackendBaseUrl;
        private string StationId => EnvironmentConfig.StationId;
        private string MapboxToken => EnvironmentConfig.MapboxAccessToken;
        private bool _securityMapInitialized = false;

        public ObservableCollection<SystemLogItem> SystemLogs { get; } = new();

        // Camera rotation variables
        private DispatcherTimer _cameraRotationTimer;
        private DispatcherTimer _cameraTimeTimer;
        private int _currentCameraIndex = 0;
		// Máy phát âm thanh cảnh báo chạy ngầm
		private readonly MediaPlayer _alarmPlayer = new MediaPlayer();
		private int _rotationCountdown = 10;
        private bool _isPaused = false;
        private string _focusedCamera = null; // Camera to focus when alert detected
        // Cameras are loaded live from MockDataService
        private readonly MockDataService _mockForCameras = MockDataService.Instance;

        // Alert filter variables
        private enum AlertFilterPeriod { Day, Week, Month }

        // Alert notification badge
        private int _pendingAlertCount = 0;
		private List<Station.Models.Alert> _pendingAlerts = new();
		private bool _alertDialogOpen = false;

        // Join request notification badge
        private int _pendingJoinRequestCount = 0;

        public MonitoringDashboardPage()
        {
            InitializeComponent();

            ViewModel = (MonitoringDashboardViewModel)DataContext;

            _themeService = ThemeService.Instance;
            _currentUserSession = CurrentUserSession.Instance;

            // Subscribe theme changes
            _themeService.ThemeChanged += OnThemeChanged;

            // Apply current theme to icons
            UpdateThemeIcons(_themeService.CurrentTheme);

            // Subscribe to alert events for flash + badge
            MockDataService.Instance.AlertGenerated += OnAlertGeneratedForUI;

            // Subscribe to device join requests for badge
            Station.Services.DataServiceLocator.Current.NewJoinRequest += OnJoinRequestForUI;

            // Initialize WebView2 + Mapbox HTML
            InitializeSecurityMap();

            // Initialize system logs
            InitializeSystemLogs();

            // Initialize camera rotation
            InitializeCameraRotation();

            ApplyCurrentUserSession();
        }

        // Function-binding helpers for empty states in MonitoringDashboardPage.xaml.
        public static Visibility VisibleWhenPositive(int count)
            => count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility VisibleWhenZero(int count)
            => count > 0 ? Visibility.Collapsed : Visibility.Visible;

        private static string BuildInitials(string name)
        {
            var parts = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => FirstTextElements(parts[0], 2).ToUpperInvariant(),
                _ => (FirstTextElements(parts[0], 1) + FirstTextElements(parts[^1], 1)).ToUpperInvariant()
            };
        }

        // Take whole text elements, not UTF-16 units, so surrogate pairs are never split.
        private static string FirstTextElements(string text, int count)
        {
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            var sb = new System.Text.StringBuilder();
            while (count-- > 0 && enumerator.MoveNext())
                sb.Append((string)enumerator.Current);
            return sb.ToString();
        }

        private void ApplyCurrentUserSession()
        {
            HeaderUserNameText.Text = string.IsNullOrWhiteSpace(_currentUserSession.FullName)
                ? (_currentUserSession.Username ?? "Tài khoản")
                : _currentUserSession.FullName;

            HeaderAvatarText.Text = BuildInitials(HeaderUserNameText.Text);

            HeaderRoleText.Text = string.IsNullOrWhiteSpace(_currentUserSession.RoleName)
                ? "Phiên đăng nhập"
                : _currentUserSession.RoleName;

            UserManagementHeaderButton.Visibility = _currentUserSession.HasAdminAccess
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateAccessControlState();
        }

        private bool HasPermission(string permissionCode)
        {
            return _currentUserSession.HasPermission(permissionCode)
                || _currentUserSession.HasAdminAccess;
        }

        private bool HasMonitorDetailAccess()
        {
            return HasPermission("MONITORING_DETAIL");
        }

        private bool HasReportingAccess()
        {
            return HasPermission("DATA_HISTORY_REPORTING");
        }

        private bool HasSystemAdminAccess()
        {
            return HasPermission("SYSTEM_ADMINISTRATION");
        }

        private void UpdateAccessControlState()
        {
            var detailVisibility = HasMonitorDetailAccess()
                ? Visibility.Visible
                : Visibility.Collapsed;
            var reportingVisibility = HasReportingAccess()
                ? Visibility.Visible
                : Visibility.Collapsed;
            var systemAdminVisibility = HasSystemAdminAccess()
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (DataPanelMenuButton != null)
            {
                DataPanelMenuButton.Visibility = detailVisibility;
            }

            if (AlertDistributionMenuButton != null)
            {
                AlertDistributionMenuButton.Visibility = detailVisibility;
            }

            if (DevicePanelMenuButton != null)
            {
                DevicePanelMenuButton.Visibility = detailVisibility;
            }

            if (CameraPanelMenuButton != null)
            {
                CameraPanelMenuButton.Visibility = detailVisibility;
            }

            if (ErrorNodesMenuButton != null)
            {
                ErrorNodesMenuButton.Visibility = reportingVisibility;
            }

            if (ConfigurationButton != null)
            {
                ConfigurationButton.Visibility = systemAdminVisibility;
            }
        }

        private void InitializeSystemLogs()
        {
            // Note: SystemLogsItems removed from XAML, keeping logs in memory
            // SystemLogsItems.ItemsSource = SystemLogs;

            // Add mock data
            AddSystemLog("✅", "Hệ thống khởi động thành công", "SYSTEM", "INFO", DateTime.Now.AddMinutes(-5));
            AddSystemLog("🔌", "RELAY_A kết nối thành công", "RELAY_A", "SUCCESS", DateTime.Now.AddMinutes(-4));
            AddSystemLog("🔌", "RELAY_B kết nối thành công", "RELAY_B", "SUCCESS", DateTime.Now.AddMinutes(-4));
            AddSystemLog("🔌", "RELAY_C kết nối thành công", "RELAY_C", "SUCCESS", DateTime.Now.AddMinutes(-3));
            AddSystemLog("📡", "S01: Radar đang hoạt động", "SENSOR", "INFO", DateTime.Now.AddMinutes(-2));
            AddSystemLog("🌡️", "S04: Nhiệt độ: 28.5°C", "SENSOR", "INFO", DateTime.Now.AddMinutes(-1));
            AddSystemLog("💧", "S05: Độ ẩm: 65%", "SENSOR", "INFO", DateTime.Now.AddSeconds(-30));
            AddSystemLog("⚠️", "S12: Phát hiện chuyển động", "ALERT", "WARNING", DateTime.Now.AddSeconds(-10));

            // Auto update logs every 10 seconds
            StartLogUpdateTimer();
        }

        private void AddSystemLog(string icon, string message, string source, string level, DateTime time)
        {
            var log = new SystemLogItem
            {
                Icon = icon,
                Message = message,
                Source = source,
                Level = level,
                Time = time.ToString("HH:mm:ss"),
                Timestamp = time
            };

            SystemLogs.Insert(0, log);

            // Keep only last 20 logs
            while (SystemLogs.Count > 20)
            {
                SystemLogs.RemoveAt(SystemLogs.Count - 1);
            }
        }

        private async void StartLogUpdateTimer()
        {
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(10000); // 10 seconds

                var random = new Random();
                var logTypes = new[]
                {
                    ("📡", "Dữ liệu cảm biến cập nhật", "SENSOR", "INFO"),
                    ("🌡️", $"Nhiệt độ: {25 + random.Next(10)}.{random.Next(10)}°C", "SENSOR", "INFO"),
                    ("💧", $"Độ ẩm: {60 + random.Next(20)}%", "SENSOR", "INFO"),
                    ("🔄", "Đồng bộ dữ liệu thành công", "SYSTEM", "SUCCESS"),
                    ("📶", $"Tín hiệu mạng: {85 + random.Next(15)}%", "NETWORK", "INFO")
                };

                var selected = logTypes[random.Next(logTypes.Length)];
                AddSystemLog(selected.Item1, selected.Item2, selected.Item3, selected.Item4, DateTime.Now);
            }
        }

        private void AdminButton_Click(object sender, RoutedEventArgs e)
        {
            OpenModuleWindow("Quản trị người dùng", typeof(UserManagementPage));
        }

		private void PlayAlarmSound(Station.Models.AlertSeverity severity)
		{
			try
			{
				string soundFileName = severity switch
				{
					Station.Models.AlertSeverity.Critical => "critical.mp3",
					Station.Models.AlertSeverity.High => "high.mp3",
					Station.Models.AlertSeverity.Medium => "medium.mp3",
					_ => "low.mp3"
				};
				var uri = new Uri($"ms-appx:///Assets/Sounds/{soundFileName}");
				_alarmPlayer.Source = MediaSource.CreateFromUri(uri);
				_alarmPlayer.Volume = severity == Station.Models.AlertSeverity.Critical ? 1.0 : 0.6;
				_alarmPlayer.Play();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ Lỗi âm thanh: {ex.Message}");
			}
		}
		private void AlertTimeFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // Get resources safely
                var transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                var whiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
                var secondaryBrush = Station.Services.ThemeBrushes.TryGet("MonitoringTextSecondaryBrush", out var secBrush)
                    ? (SolidColorBrush)secBrush
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 139, 148, 158));
                var borderBrush = Station.Services.ThemeBrushes.TryGet("MonitoringBorderBrush", out var brdBrush)
                    ? (SolidColorBrush)brdBrush
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 54, 61));
                var accentBrush = Station.Services.ThemeBrushes.TryGet("MonitoringAccentButtonBrush", out var accBrush)
                    ? (SolidColorBrush)accBrush
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 33, 150, 243));

                // Reset all filter buttons
                AlertDayButton.Background = transparentBrush;
                AlertDayButton.Foreground = secondaryBrush;
                AlertDayButton.BorderBrush = borderBrush;
                AlertDayButton.BorderThickness = new Thickness(1);

                AlertWeekButton.Background = transparentBrush;
                AlertWeekButton.Foreground = secondaryBrush;
                AlertWeekButton.BorderBrush = borderBrush;
                AlertWeekButton.BorderThickness = new Thickness(1);

                AlertMonthButton.Background = transparentBrush;
                AlertMonthButton.Foreground = secondaryBrush;
                AlertMonthButton.BorderBrush = borderBrush;
                AlertMonthButton.BorderThickness = new Thickness(1);

                // Set clicked button as active
                button.Background = accentBrush;
                button.Foreground = whiteBrush;
                button.BorderThickness = new Thickness(0);

                // Get filter type
                string filterTag = button.Tag?.ToString() ?? "Day";
                Debug.WriteLine($"Alert time filter changed to: {filterTag}");

                // TODO: Update alert distribution chart based on time filter
            }
        }

		private async void InitializeSecurityMap()
		{
			try
			{
				await SecurityMapWebView.EnsureCoreWebView2Async();

				// CHỈ CẦN DUY NHẤT 2 DÒNG NÀY ĐỂ MỞ ĐƯỜNG CHO JS ĐỌC FILE
				var assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
				SecurityMapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
					"app.local", assetsPath, CoreWebView2HostResourceAccessKind.Allow);

				// Các dòng còn lại giữ nguyên
				SecurityMapWebView.CoreWebView2.Navigate("https://app.local/Map/map.html");

				SecurityMapWebView.NavigationCompleted += SecurityMapWebView_NavigationCompleted;
				SecurityMapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Lỗi Init Map: {ex.Message}");
			}
		}

		private async void SecurityMapWebView_NavigationCompleted(
            WebView2 sender,
            CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
            {
                Debug.WriteLine($"SecurityMap navigation failed: {args.WebErrorStatus}");
                return;
            }

            // Đảm bảo chỉ init 1 lần
            if (_securityMapInitialized)
                return;

            _securityMapInitialized = true;

            try
            {
                // Chờ 1 chút cho JS khởi tạo xong
                await System.Threading.Tasks.Task.Delay(300);

                SendInitMessageToMap();
                ApplyThemeToSecurityMap(_themeService.CurrentTheme);

                Debug.WriteLine("Security map initialized & config sent.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in NavigationCompleted: {ex.Message}");
            }
        }

        /// <summary>
        /// Gửi cấu hình ban đầu (backend, station, token) sang HTML (map.html)
        /// JS trong map.html sẽ nhận qua window.chrome.webview.addEventListener('message', ...)
        /// </summary>
        private void SendInitMessageToMap()
        {
            try
            {
                if (SecurityMapWebView?.CoreWebView2 == null)
                    return;

                var initPayload = new
                {
                    type = "init",
                    backend = BackendBaseUrl,
                    station = StationId,
                    token = MapboxToken
                };

                var json = JsonSerializer.Serialize(initPayload);
                SecurityMapWebView.CoreWebView2.PostWebMessageAsJson(json);

                Debug.WriteLine($"Sent init message to map: {json}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending init message: {ex.Message}");
            }
        }

		private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
		{
			try
			{
				var message = args.TryGetWebMessageAsString();
				if (string.IsNullOrEmpty(message)) return;

				var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
				var data = JsonSerializer.Deserialize<SecurityMapMessage>(message, options);

				if (data == null) return;

				switch (data.Type?.ToLower())
				{
					case "mapready":
						Debug.WriteLine("Security map is ready");
						break;
					case "viewcamera":
						HandleViewCamera(data.CameraId, data.NodeId);
						break;
					case "managedevice":
						HandleManageDevice(data.NodeId);
						break;

					// --- THÊM TRƯỜNG HỢP NÀY ĐỂ LƯU FILE ---
					case "node-moved":
						SaveNodePosition(data.NodeId, data.Lng, data.Lat);
						break;
						// --------------------------------------
				}
			}
			catch (Exception ex) { Debug.WriteLine($"Error: {ex.Message}"); }
		}
		private async void HandleViewCamera(string? cameraId, string? nodeId)
        {
            if (!HasMonitorDetailAccess())
                return;

            if (string.IsNullOrEmpty(cameraId))
                return;

            Debug.WriteLine($"View camera: {cameraId} for node: {nodeId}");

            try
            {
                var playbackDialog = new PlaybackDialog(cameraId)
                {
                    XamlRoot = this.XamlRoot
                };

                await playbackDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening PlaybackDialog: {ex.Message}");

                var errorDialog = new ContentDialog
                {
                    Title = "Lỗi",
                    Content = $"Không thể mở camera: {ex.Message}",
                    CloseButtonText = "Đóng",
                    XamlRoot = this.XamlRoot
                };

                await errorDialog.ShowAsync();
            }
        }

        private async void HandleManageDevice(string? nodeId)
        {
            if (!HasMonitorDetailAccess())
                return;

            if (string.IsNullOrEmpty(nodeId))
                return;

            Debug.WriteLine($"Manage device for node: {nodeId}");

            string deviceType = GetDeviceTypeFromNodeId(nodeId);

            var dialog = new DeviceControlDialog(nodeId, deviceType)
            {
                XamlRoot = this.XamlRoot
            };

            await dialog.ShowAsync();
        }

        private string GetDeviceTypeFromNodeId(string nodeId)
        {
            if (nodeId.StartsWith("CAM", StringComparison.OrdinalIgnoreCase) ||
                nodeId.Contains("Camera", StringComparison.OrdinalIgnoreCase))
            {
                return "Camera";
            }
            else if (nodeId.StartsWith("SEN", StringComparison.OrdinalIgnoreCase) ||
                     nodeId.Contains("Sensor", StringComparison.OrdinalIgnoreCase))
            {
                return "Sensor";
            }
            else if (nodeId.StartsWith("RAD", StringComparison.OrdinalIgnoreCase) ||
                     nodeId.Contains("Radar", StringComparison.OrdinalIgnoreCase))
            {
                return "Radar";
            }

            return "Sensor";
        }

        /// <summary>
        /// Update 1 node trên map (JS phải có hàm updateNode(node))
        /// </summary>
        public async void UpdateNodeInMap(string nodeId, object nodeData)
        {
            try
            {
                if (SecurityMapWebView?.CoreWebView2 == null)
                    return;

                var json = JsonSerializer.Serialize(nodeData);
                var script = $"updateNode({json})";

                await SecurityMapWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating node in map: {ex.Message}");
            }
        }

        /// <summary>
        /// Update nhiều node trên map (JS phải có hàm updateNodes(nodes))
        /// </summary>
        public async void UpdateNodesInMap(object[] nodesData)
        {
            try
            {
                if (SecurityMapWebView?.CoreWebView2 == null)
                    return;

                var json = JsonSerializer.Serialize(nodesData);
                var script = $"updateNodes({json})";

                await SecurityMapWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating nodes in map: {ex.Message}");
            }
        }

        // ==== Relay Station Card Events ====

        private void RelayCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.BorderThickness = new Thickness(2);
                border.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);
            }
        }

        private void RelayCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.BorderThickness = new Thickness(1);
            }
        }

        private async void RelayCard_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Border border && border.Tag is string relayId)
            {
                Debug.WriteLine($"Relay station clicked: {relayId}");
                await ShowRelayDataDialog(relayId);
            }
        }

        private async System.Threading.Tasks.Task ShowRelayDataDialog(string relayId)
        {
            if (!HasMonitorDetailAccess())
                return;

            try
            {
                var dialog = new DeviceDataDialog(relayId)
                {
                    XamlRoot = this.XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening relay data dialog: {ex.Message}");

                var errorDialog = new ContentDialog
                {
                    Title = "Lỗi",
                    Content = $"Không thể mở thông tin trạm: {ex.Message}",
                    CloseButtonText = "Đóng",
                    XamlRoot = this.XamlRoot
                };

                await errorDialog.ShowAsync();
            }
        }

        // ==== Phần menu & theme giữ nguyên ====

        private void DataPanelMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasMonitorDetailAccess())
                return;

            OpenModuleWindow("Giám sát dữ liệu", typeof(SensorChartsPage));
        }

        private void TrendPanelMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasReportingAccess())
                return;

            OpenModuleWindow("Phân tích xu hướng", typeof(AnalyticsReportPage));
        }

        private void AlertPanelMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasMonitorDetailAccess())
                return;

            OpenModuleWindow("Cảnh báo", typeof(AlertsPage));
        }

        private void CameraMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasMonitorDetailAccess())
                return;

            OpenModuleWindow("Camera giám sát", typeof(LiveVideoPage));
        }

        private void CameraPanelMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasMonitorDetailAccess())
                return;

            OpenModuleWindow("Camera giám sát", typeof(LiveVideoPage));
        }

        private void DevicePanelMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasMonitorDetailAccess())
                return;

            OpenModuleWindow("Thiết bị", typeof(DevicesPage));
        }

        private void ConfigurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasSystemAdminAccess())
                return;

            OpenModuleWindow("Cấu hình", typeof(ConfigurationPage));
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _themeService.ToggleTheme();
        }

        private async void AccountInfoButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowAccountInfoDialogAsync();
        }

        private async Task ShowAccountInfoDialogAsync()
        {
            while (true)
            {
                var panel = new Grid
                {
                    ColumnSpacing = 14,
                    RowSpacing = 14
                };
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var usernameBox = CreateAccountDisplayTextBox("Tài khoản", _currentUserSession.Username ?? "Không xác định");
                var fullNameBox = CreateAccountDisplayTextBox("Họ tên", _currentUserSession.FullName ?? _currentUserSession.Username ?? "Không xác định");
                var roleBox = CreateAccountDisplayTextBox("Vai trò", _currentUserSession.RoleName ?? "Không xác định");
                var statusBox = CreateAccountDisplayTextBox("Trạng thái", _currentUserSession.IsAuthenticated ? "Đang đăng nhập" : "Chưa đăng nhập");

                Grid.SetColumn(usernameBox, 0);
                Grid.SetRow(usernameBox, 0);
                Grid.SetColumn(fullNameBox, 1);
                Grid.SetRow(fullNameBox, 0);
                Grid.SetColumn(roleBox, 0);
                Grid.SetRow(roleBox, 1);
                Grid.SetColumn(statusBox, 1);
                Grid.SetRow(statusBox, 1);

                panel.Children.Add(usernameBox);
                panel.Children.Add(fullNameBox);
                panel.Children.Add(roleBox);
                panel.Children.Add(statusBox);

                var dialog = CreateAccountDialog(
                    "Thông tin tài khoản",
                    panel,
                    primaryButtonText: "Chỉnh sửa thông tin",
                    secondaryButtonText: "Đổi mật khẩu",
                    closeButtonText: "Đóng");

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    await ShowEditProfileDialogAsync();
                    continue;
                }

                if (result == ContentDialogResult.Secondary)
                {
                    await ShowChangePasswordDialogAsync();
                    continue;
                }

                break;
            }
        }

        private async Task ShowEditProfileDialogAsync()
        {
            var usernameBox = CreateAccountTextBox("Tài khoản", _currentUserSession.Username ?? string.Empty, "Nhập tài khoản");
            var fullNameBox = CreateAccountTextBox("Họ tên", _currentUserSession.FullName ?? _currentUserSession.Username ?? string.Empty, "Nhập họ tên");

            var panel = new StackPanel
            {
                Spacing = 14
            };
            panel.Children.Add(usernameBox);
            panel.Children.Add(fullNameBox);

            var dialog = CreateAccountDialog(
                "Chỉnh sửa thông tin tài khoản",
                panel,
                primaryButtonText: "Lưu",
                secondaryButtonText: "Hủy");

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(usernameBox.Text))
                {
                    args.Cancel = true;
                    usernameBox.Focus(FocusState.Programmatic);
                    return;
                }

                if (string.IsNullOrWhiteSpace(fullNameBox.Text))
                {
                    args.Cancel = true;
                    fullNameBox.Focus(FocusState.Programmatic);
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                var username = usernameBox.Text.Trim();
                var fullName = fullNameBox.Text.Trim();

                await _userApiService.UpdateProfileAsync(username, fullName);
                AuthSession.UpdateProfile(username);
                _currentUserSession.UpdateProfile(username, fullName);
                ApplyCurrentUserSession();
            }
            catch (Exception ex)
            {
                await ShowAccountErrorDialogAsync("Không thể cập nhật thông tin", ex.Message);
            }
        }

        private async Task ShowChangePasswordDialogAsync()
        {
            var currentPasswordBox = CreateAccountPasswordBox("Mật khẩu hiện tại", "Nhập mật khẩu hiện tại");
            var newPasswordBox = CreateAccountPasswordBox("Mật khẩu mới", "Nhập mật khẩu mới");
            var confirmPasswordBox = CreateAccountPasswordBox("Nhập lại mật khẩu mới", "Nhập lại mật khẩu mới");
            var hint = new TextBlock
            {
                Text = "Mật khẩu mới nên có ít nhất 6 ký tự.",
                FontSize = 12,
                Foreground = ThemeBrush("TextSecondaryBrush")
            };

            var panel = new StackPanel
            {
                Spacing = 14
            };
            panel.Children.Add(currentPasswordBox);
            panel.Children.Add(newPasswordBox);
            panel.Children.Add(confirmPasswordBox);
            panel.Children.Add(hint);

            var dialog = CreateAccountDialog(
                "Đổi mật khẩu",
                panel,
                primaryButtonText: "Cập nhật",
                secondaryButtonText: "Hủy");

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(currentPasswordBox.Password))
                {
                    args.Cancel = true;
                    currentPasswordBox.Focus(FocusState.Programmatic);
                    return;
                }

                if (string.IsNullOrWhiteSpace(newPasswordBox.Password))
                {
                    args.Cancel = true;
                    newPasswordBox.Focus(FocusState.Programmatic);
                    return;
                }

                if (newPasswordBox.Password.Length < 6)
                {
                    args.Cancel = true;
                    newPasswordBox.Focus(FocusState.Programmatic);
                    return;
                }

                if (!string.Equals(newPasswordBox.Password, confirmPasswordBox.Password, StringComparison.Ordinal))
                {
                    args.Cancel = true;
                    confirmPasswordBox.Focus(FocusState.Programmatic);
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                await _userApiService.ChangePasswordAsync(
                    currentPasswordBox.Password,
                    newPasswordBox.Password);
            }
            catch (Exception ex)
            {
                await ShowAccountErrorDialogAsync("Không thể đổi mật khẩu", ex.Message);
            }
        }

        private ContentDialog CreateAccountDialog(
            string title,
            UIElement content,
            string? primaryButtonText = null,
            string? secondaryButtonText = null,
            string? closeButtonText = null)
        {
            var surfaceBrush = DialogSurfaceBrush();
            var borderBrush = DialogBorderBrush();
            var primaryButtonBrush = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
            var primaryButtonHoverBrush = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
            var primaryButtonPressedBrush = new SolidColorBrush(Color.FromArgb(255, 29, 78, 216));

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new Border
                {
                    Width = 520,
                    Padding = new Thickness(4, 4, 4, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                    Child = content
                },
                PrimaryButtonText = primaryButtonText,
                SecondaryButtonText = secondaryButtonText,
                CloseButtonText = closeButtonText,
                DefaultButton = string.IsNullOrWhiteSpace(primaryButtonText)
                    ? ContentDialogButton.Close
                    : ContentDialogButton.Primary,
                RequestedTheme = _themeService.CurrentTheme,
                XamlRoot = this.XamlRoot
            };

            dialog.Resources["ContentDialogBackground"] = surfaceBrush;
            dialog.Resources["ContentDialogBorderBrush"] = borderBrush;
            dialog.Resources["ContentDialogBorderThemeBrush"] = borderBrush;
            dialog.Resources["ContentDialogTitleForeground"] = ThemeBrush("TextPrimaryBrush");
            dialog.Resources["DefaultTextForegroundThemeBrush"] = ThemeBrush("TextPrimaryBrush");
            dialog.Resources["SystemControlPageBackgroundChromeLowBrush"] = surfaceBrush;
            dialog.Resources["SystemControlPageBackgroundChromeMediumBrush"] = surfaceBrush;
            dialog.Resources["SystemControlPageBackgroundChromeHighBrush"] = surfaceBrush;
            dialog.Resources["SystemControlBackgroundBaseLowBrush"] = surfaceBrush;
            dialog.Resources["SystemControlAltHighAcrylicWindowBrush"] = surfaceBrush;
            dialog.Resources["ContentDialogCommandSpaceBackground"] = surfaceBrush;
            dialog.Resources["ContentDialogCommandSpaceBorderBrush"] = borderBrush;
            dialog.Resources["ContentDialogButtonPrimaryBackground"] = primaryButtonBrush;
            dialog.Resources["ContentDialogButtonPrimaryBackgroundPointerOver"] = primaryButtonHoverBrush;
            dialog.Resources["ContentDialogButtonPrimaryBackgroundPressed"] = primaryButtonPressedBrush;
            dialog.Resources["ContentDialogButtonPrimaryBorderBrush"] = borderBrush;
            dialog.Resources["ContentDialogButtonPrimaryBorderBrushPointerOver"] = borderBrush;
            dialog.Resources["ContentDialogButtonPrimaryBorderBrushPressed"] = borderBrush;
            dialog.Resources["ContentDialogButtonPrimaryForeground"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            dialog.Resources["ContentDialogButtonPrimaryForegroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            dialog.Resources["ContentDialogButtonPrimaryForegroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            dialog.Resources["AccentButtonBackground"] = primaryButtonBrush;
            dialog.Resources["AccentButtonBackgroundPointerOver"] = primaryButtonHoverBrush;
            dialog.Resources["AccentButtonBackgroundPressed"] = primaryButtonPressedBrush;
            dialog.Resources["AccentButtonBorderBrush"] = borderBrush;
            dialog.Resources["AccentButtonBorderBrushPointerOver"] = borderBrush;
            dialog.Resources["AccentButtonBorderBrushPressed"] = borderBrush;
            dialog.Resources["AccentButtonForeground"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            dialog.Resources["AccentButtonForegroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            dialog.Resources["AccentButtonForegroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            dialog.PrimaryButtonStyle = CreateAccountPrimaryButtonStyle();
            dialog.SecondaryButtonStyle = CreateAccountSecondaryButtonStyle();
            dialog.CloseButtonStyle = CreateAccountSecondaryButtonStyle();
            dialog.Opened += (_, _) =>
            {
                NormalizeDialogActionButtons(dialog);
                DispatcherQueue.TryEnqueue(() => NormalizeDialogActionButtons(dialog));
            };

            return dialog;
        }

        private TextBox CreateAccountTextBox(string header, string value, string placeholder)
        {
            var normalBackground = DialogInputBackgroundBrush();
            var focusedBackground = DialogInputBackgroundBrush();
            var pointerBackground = DialogInputBackgroundBrush();
            var normalBorder = DialogInputBorderBrush();
            var focusedBorder = DialogInputBorderFocusedBrush();
            var pointerBorder = DialogInputBorderFocusedBrush();
            var foreground = DialogInputForegroundBrush();
            var placeholderForeground = DialogInputPlaceholderBrush();
            var headerForeground = DialogInputHeaderBrush();

            var box = new TextBox
            {
                Style = AppStyle("StandardTextBoxStyle"),
                Header = header,
                Text = value,
                PlaceholderText = placeholder,
                MinHeight = 40,
                Padding = new Thickness(12, 10, 12, 10),
                Background = normalBackground,
                Foreground = foreground,
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(0.8),
                CornerRadius = new CornerRadius(8)
            };

            box.Resources["TextControlBackground"] = normalBackground;
            box.Resources["TextControlBackgroundFocused"] = focusedBackground;
            box.Resources["TextControlBackgroundPointerOver"] = pointerBackground;
            box.Resources["TextControlBackgroundPressed"] = pointerBackground;
            box.Resources["TextControlBorderBrush"] = normalBorder;
            box.Resources["TextControlBorderBrushFocused"] = focusedBorder;
            box.Resources["TextControlBorderBrushPointerOver"] = pointerBorder;
            box.Resources["TextControlBorderBrushPressed"] = focusedBorder;
            box.Resources["TextControlBorderThemeThickness"] = new Thickness(0.8);
            box.Resources["TextControlBorderThemeThicknessPointerOver"] = new Thickness(0.8);
            box.Resources["TextControlBorderThemeThicknessPressed"] = new Thickness(0.8);
            box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0.8);
            box.Resources["TextControlForeground"] = foreground;
            box.Resources["TextControlForegroundPointerOver"] = foreground;
            box.Resources["TextControlForegroundFocused"] = foreground;
            box.Resources["TextControlPlaceholderForeground"] = placeholderForeground;
            box.Resources["TextControlPlaceholderForegroundPointerOver"] = placeholderForeground;
            box.Resources["TextControlPlaceholderForegroundFocused"] = placeholderForeground;
            box.Resources["TextControlHeaderForeground"] = headerForeground;

            return box;
        }

        private PasswordBox CreateAccountPasswordBox(string header, string placeholder)
        {
            var normalBackground = DialogInputBackgroundBrush();
            var focusedBackground = DialogInputBackgroundBrush();
            var pointerBackground = DialogInputBackgroundBrush();
            var normalBorder = DialogInputBorderBrush();
            var focusedBorder = DialogInputBorderFocusedBrush();
            var pointerBorder = DialogInputBorderFocusedBrush();
            var foreground = DialogInputForegroundBrush();
            var placeholderForeground = DialogInputPlaceholderBrush();
            var headerForeground = DialogInputHeaderBrush();
            var buttonForeground = DialogInputForegroundBrush();
            var buttonBackground = DialogInputBackgroundBrush();
            var buttonBorder = DialogInputBorderBrush();
            var buttonBorderFocused = DialogInputBorderFocusedBrush();

            var box = new PasswordBox
            {
                Header = header,
                PlaceholderText = placeholder,
                MinHeight = 40,
                Padding = new Thickness(12, 10, 12, 10),
                Background = normalBackground,
                Foreground = foreground,
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(0.8),
                CornerRadius = new CornerRadius(8),
                PasswordRevealMode = PasswordRevealMode.Peek
            };

            box.Resources["PasswordBoxBackground"] = normalBackground;
            box.Resources["PasswordBoxBackgroundFocused"] = focusedBackground;
            box.Resources["PasswordBoxBackgroundPointerOver"] = pointerBackground;
            box.Resources["PasswordBoxBackgroundPressed"] = pointerBackground;
            box.Resources["PasswordBoxBorderBrush"] = normalBorder;
            box.Resources["PasswordBoxBorderBrushFocused"] = focusedBorder;
            box.Resources["PasswordBoxBorderBrushPointerOver"] = pointerBorder;
            box.Resources["PasswordBoxBorderBrushPressed"] = focusedBorder;
            box.Resources["TextControlBorderThemeThickness"] = new Thickness(0.8);
            box.Resources["TextControlBorderThemeThicknessPointerOver"] = new Thickness(0.8);
            box.Resources["TextControlBorderThemeThicknessPressed"] = new Thickness(0.8);
            box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0.8);
            box.Resources["PasswordBoxForeground"] = foreground;
            box.Resources["PasswordBoxForegroundPointerOver"] = foreground;
            box.Resources["PasswordBoxForegroundFocused"] = foreground;
            box.Resources["PasswordBoxPlaceholderForeground"] = placeholderForeground;
            box.Resources["PasswordBoxPlaceholderForegroundPointerOver"] = placeholderForeground;
            box.Resources["PasswordBoxPlaceholderForegroundFocused"] = placeholderForeground;
            box.Resources["TextControlHeaderForeground"] = headerForeground;
            box.Resources["ButtonForeground"] = buttonForeground;
            box.Resources["ButtonForegroundPointerOver"] = buttonForeground;
            box.Resources["ButtonForegroundPressed"] = buttonForeground;
            box.Resources["ButtonBackground"] = buttonBackground;
            box.Resources["ButtonBackgroundPointerOver"] = buttonBackground;
            box.Resources["ButtonBackgroundPressed"] = buttonBackground;
            box.Resources["ButtonBorderBrush"] = buttonBorder;
            box.Resources["ButtonBorderBrushPointerOver"] = buttonBorderFocused;
            box.Resources["ButtonBorderBrushPressed"] = buttonBorderFocused;

            return box;
        }

        private TextBox CreateAccountDisplayTextBox(string header, string value)
        {
            var box = CreateAccountTextBox(header, value, string.Empty);
            box.IsReadOnly = true;
            box.IsTabStop = false;
            box.PlaceholderText = string.Empty;
            return box;
        }

        private async Task ShowAccountErrorDialogAsync(string title, string message)
        {
            var dialog = CreateAccountDialog(
                title,
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextPrimaryBrush")
                },
                closeButtonText: "Đóng");

            await dialog.ShowAsync();
        }

        private static UIElement CreateAccountInfoRow(string label, string value)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };

            var valueBlock = new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap
            };

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(valueBlock, 1);
            grid.Children.Add(labelBlock);
            grid.Children.Add(valueBlock);

            return grid;
        }

        private Brush ResourceBrush(string key)
        {
            return Station.Services.ThemeBrushes.TryGet(key, out var value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        }

        private SolidColorBrush DialogSurfaceBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 31, 39, 60));
            }

            return ThemeBrush("BackgroundPrimaryBrush");
        }

        private SolidColorBrush DialogBorderBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 26, 49, 77));
            }

            return ThemeBrush("BorderLightBrush");
        }

        private SolidColorBrush DialogInputBackgroundBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 23, 30, 51));
            }

            return ThemeBrush("BackgroundSecondaryBrush");
        }

        private SolidColorBrush DialogInputBorderBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 45, 50, 56));
            }

            return ThemeBrush("TextControlBorderBrush");
        }

        private SolidColorBrush DialogInputBorderFocusedBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 41, 121, 255));
            }

            return ThemeBrush("AccentBrush");
        }

        private SolidColorBrush DialogInputForegroundBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 226, 232, 240));
            }

            return ThemeBrush("TextPrimaryBrush");
        }

        private SolidColorBrush DialogInputPlaceholderBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
            }

            return ThemeBrush("TextSecondaryBrush");
        }

        private SolidColorBrush DialogInputHeaderBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
            }

            return ThemeBrush("TextPrimaryBrush");
        }

        private bool IsDarkThemeActive()
        {
            return _themeService.CurrentTheme != ElementTheme.Light;
        }

        private SolidColorBrush ThemeBrush(string key)
        {
            return Station.Services.ThemeBrushes.Get(key) as SolidColorBrush
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 50, 56));
        }

        private Style AppStyle(string key)
        {
            return Station.Services.ThemeBrushes.Get(key) as Style
                ?? new Style(typeof(TextBox));
        }

        private Style CreateAccountPrimaryButtonStyle()
        {
            var style = new Style(typeof(Button));
            var primary = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
            style.Setters.Add(new Setter(Control.BackgroundProperty, primary));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBrush("BorderLightBrush")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(20, 0, 20, 0)));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 44d));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        private Style CreateAccountSecondaryButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, ThemeBrush("BackgroundSecondaryBrush")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, ThemeBrush("TextPrimaryBrush")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBrush("BorderLightBrush")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(20, 0, 20, 0)));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 44d));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        private void NormalizeDialogActionButtons(ContentDialog dialog)
        {
            var buttons = new List<Button>();

            foreach (var buttonName in new[] { "PrimaryButton", "SecondaryButton", "CloseButton" })
            {
                if (FindNamedDescendant<Button>(dialog, buttonName) is not Button button)
                {
                    continue;
                }

                buttons.Add(button);
                button.Height = 44;
                button.MinHeight = 44;
                button.MaxHeight = 44;
                button.Padding = new Thickness(20, 0, 20, 0);
                button.Margin = new Thickness(0);
                button.VerticalAlignment = VerticalAlignment.Center;
                button.VerticalContentAlignment = VerticalAlignment.Center;
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
            }

            var actionButtons = buttons
                .Where(button => string.Equals(button.Name, "PrimaryButton", StringComparison.Ordinal)
                    || string.Equals(button.Name, "SecondaryButton", StringComparison.Ordinal)
                    || string.Equals(button.Name, "CloseButton", StringComparison.Ordinal))
                .ToList();

            if (actionButtons.Count < 2)
            {
                return;
            }

            var uniformWidth = Math.Ceiling(actionButtons.Max(button => Math.Max(button.ActualWidth, button.MinWidth)));
            if (uniformWidth <= 0)
            {
                uniformWidth = 220;
            }

            foreach (var button in actionButtons)
            {
                button.Width = uniformWidth;
                button.MinWidth = uniformWidth;
                button.MaxWidth = uniformWidth;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }

        private static T? FindNamedDescendant<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            if (root is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
            {
                return typed;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var match = FindNamedDescendant<T>(VisualTreeHelper.GetChild(root, i), name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app && app.m_window is MainWindow mainWindow)
            {
                await mainWindow.LogoutAsync();
            }
        }

        private void OnThemeChanged(object? sender, ElementTheme theme)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateThemeIcons(theme);
                ApplyThemeToSecurityMap(theme);
            });
        }

        private async void ApplyThemeToSecurityMap(ElementTheme theme)
        {
            try
            {
                if (SecurityMapWebView?.CoreWebView2 == null)
                    return;

                var themeString = theme == ElementTheme.Light ? "Light" : "Dark";
                var script = $"setTheme('{themeString}')";

                await SecurityMapWebView.CoreWebView2.ExecuteScriptAsync(script);
                Debug.WriteLine($"Applied theme to security map: {themeString}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying theme to security map: {ex.Message}");
            }
        }

        private void UpdateThemeIcons(ElementTheme theme)
        {
            if (theme == ElementTheme.Dark)
            {
                MoonIcon.Visibility = Visibility.Visible;
                SunIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                MoonIcon.Visibility = Visibility.Collapsed;
                SunIcon.Visibility = Visibility.Visible;
            }
        }

        private void OpenModuleWindow(string title, Type pageType)
        {
            try
            {
                if (Application.Current is App app && app.m_window is MainWindow mainWindow)
                {
                    if (pageType == typeof(SensorChartsPage))
                    {
                        mainWindow.OpenPageInNewWindow<SensorChartsPage>(title);
                    }
                    else if (pageType == typeof(AlertsPage))
                    {
                        mainWindow.OpenPageInNewWindow<AlertsPage>(title);
                    }
                    else if (pageType == typeof(LiveVideoPage))
                    {
                        mainWindow.OpenPageInNewWindow<LiveVideoPage>(title);
                    }
                    else if (pageType == typeof(DevicesPage))
                    {
                        mainWindow.OpenPageInNewWindow<DevicesPage>(title);
                    }
                    else if (pageType == typeof(ConfigurationPage))
                    {
                        mainWindow.OpenPageInNewWindow<ConfigurationPage>(title);
                    }
                    else if (pageType == typeof(UserManagementPage))
                    {
                        mainWindow.OpenPageInNewWindow<UserManagementPage>(title);
                    }
                    else if (pageType == typeof(AnalyticsReportPage))
                    {
                        mainWindow.OpenPageInNewWindow<AnalyticsReportPage>(title);
                    }
                    else
                    {
                        Debug.WriteLine($"Unknown page type: {pageType.Name}");
                    }
                }
                else
                {
                    Debug.WriteLine("Could not get MainWindow instance");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening module window: {ex.Message}");
            }
        }

		private void SaveNodePosition(string nodeId, double lng, double lat)
		{
			try
			{
				string filePath = "";

#if DEBUG
				// 1. Lấy vị trí Bản Photo đang chạy (Thường nằm tít trong bin/x64/Debug/...)
				DirectoryInfo? currentDir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

				// 2. Tự động leo ngược lên trên cho đến khi tìm thấy thư mục gốc chứa "Assets"
				while (currentDir != null)
				{
					// Kiểm tra xem ở đây có thư mục "Assets/Map" chưa?
					string checkPath = Path.Combine(currentDir.FullName, "Assets", "Map", "nodes.json");

					// Nếu tìm thấy file nodes.json ở đây, nghĩa là đã về tới nhà (Bản Gốc)
					if (File.Exists(checkPath))
					{
						filePath = checkPath;
						break; // Dừng việc leo ngược lại
					}

					// Nếu chưa thấy, lùi lên một cấp thư mục (Back)
					currentDir = currentDir.Parent;
				}

				// Nếu leo hết cỡ vẫn không thấy (hiếm khi xảy ra), dùng tạm Bản Photo
				if (string.IsNullOrEmpty(filePath))
				{
					filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Map", "nodes.json");
				}
#else
        // Khi đóng gói đem cài cho khách hàng (Release) thì dùng đường dẫn cài đặt
        filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Map", "nodes.json");
#endif

				// 3. Tiến hành ghi đè tọa độ mới vào file đã tìm được
				if (!File.Exists(filePath)) return;

				string jsonContent = File.ReadAllText(filePath);
				using var doc = JsonDocument.Parse(jsonContent);
				var root = doc.RootElement.Clone();

				var features = root.GetProperty("features").EnumerateArray().Select(f => {
					var props = f.GetProperty("properties");
					if (props.GetProperty("id").GetString() == nodeId)
					{
						// Thay thế tọa độ cũ bằng tọa độ mới (lng, lat)
						return new
						{
							type = "Feature",
							geometry = new { type = "Point", coordinates = new[] { lng, lat } },
							properties = f.GetProperty("properties")
						};
					}
					return (object)f;
				}).ToList();

				var updatedData = new { type = "FeatureCollection", features = features };

				// Ghi lại vào file với định dạng đẹp (xuống dòng, lùi lề)
				string updatedJson = JsonSerializer.Serialize(updatedData, new JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(filePath, updatedJson);

				Debug.WriteLine($"✅ Đã lưu CẬP NHẬT vào: {filePath}");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"❌ Lỗi: {ex.Message}");
			}
		}
		private class SecurityMapMessage
		{
			[JsonPropertyName("type")]
			public string? Type { get; set; }

			[JsonPropertyName("cameraId")]
			public string? CameraId { get; set; }

			[JsonPropertyName("nodeId")]
			public string? NodeId { get; set; }

			// Thêm 2 dòng này
			[JsonPropertyName("lng")]
			public double Lng { get; set; }

			[JsonPropertyName("lat")]
			public double Lat { get; set; }
		}
		#region Camera Rotation Methods

		private void InitializeCameraRotation()
        {
            // Timer for camera rotation
            _cameraRotationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _cameraRotationTimer.Tick += CameraRotationTimer_Tick;
            _cameraRotationTimer.Start();

            // Timer for camera time display
            _cameraTimeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _cameraTimeTimer.Tick += CameraTimeTimer_Tick;
            _cameraTimeTimer.Start();

            // Display first camera
            UpdateCurrentCamera();

            // Simulate alert detection after 15 seconds
            SimulateAlertDetection();
        }

        private void CameraRotationTimer_Tick(object sender, object e)
        {
            if (_isPaused || _focusedCamera != null)
                return;

            var cameras = _mockForCameras.Cameras;
            if (cameras.Count == 0) return;

            _rotationCountdown--;

            if (_rotationCountdown <= 0)
            {
                _currentCameraIndex = (_currentCameraIndex + 1) % cameras.Count;
                UpdateCurrentCamera();
                _rotationCountdown = 10;
            }

            var nextIndex = (_currentCameraIndex + 1) % cameras.Count;
            NextCameraInfo.Text = $"Tiếp: {cameras[nextIndex].CameraId} ({_rotationCountdown}s)";
        }

        private void CameraTimeTimer_Tick(object sender, object e)
        {
            CurrentCameraTime.Text = DateTime.Now.ToString("HH:mm:ss");
        }

        private void UpdateCurrentCamera()
        {
            var cameras = _mockForCameras.Cameras;
            if (cameras.Count == 0) return;

            Services.SimulatedCamera cam;
            if (_focusedCamera != null)
            {
                cam = cameras.FirstOrDefault(c => c.CameraId == _focusedCamera)
                      ?? cameras[_currentCameraIndex % cameras.Count];
            }
            else
            {
                cam = cameras[_currentCameraIndex % cameras.Count];
            }

            CurrentCameraName.Text = $"{cam.CameraId} — {cam.Location}";
            bool isOnline = cam.IsOnline;

            // Update status badge
            if (isOnline)
            {
                CurrentCameraStatus.Text = "Online";
                CurrentCameraStatusBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94)); // Green
                NoSignalOverlay.Visibility = Visibility.Collapsed;
            }
            else
            {
                CurrentCameraStatus.Text = "Offline";
                CurrentCameraStatusBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68)); // Red
                NoSignalOverlay.Visibility = Visibility.Visible;
            }

            // Update rotation info
            if (_focusedCamera != null)
            {
                CameraRotationInfo.Text = "Đang focus (Cảnh báo)";
                NextCameraInfo.Text = "Chờ xử lý...";
            }
            else if (_isPaused)
            {
                CameraRotationInfo.Text = "⏸️ Đã tạm dừng";
                NextCameraInfo.Text = "";
            }
            else
            {
                CameraRotationInfo.Text = "Tự động: 10s";
            }
        }

        private void CameraPauseButton_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;

            if (_isPaused)
            {
                CameraPauseIcon.Glyph = "\uE768"; // Play icon
                CameraRotationInfo.Text = "⏸️ Đã tạm dừng";
                NextCameraInfo.Text = "";
            }
            else
            {
                CameraPauseIcon.Glyph = "\uE769"; // Pause icon
                CameraRotationInfo.Text = "Tự động: 10s";
                _rotationCountdown = 10;
            }
        }

        private async void SimulateAlertDetection()
        {
            await System.Threading.Tasks.Task.Delay(15000);

            // Focus on first online camera from MockDataService
            var firstOnline = _mockForCameras.Cameras.FirstOrDefault(c => c.IsOnline);
            if (firstOnline == null) return;
            FocusOnCameraAlert(firstOnline.CameraId, "Phát hiện chuyển động bất thường");

            // After 10 seconds, clear focus and resume rotation
            await System.Threading.Tasks.Task.Delay(10000);
            ClearCameraFocus();
        }

        private void FocusOnCameraAlert(string cameraName, string alertMessage)
        {
            _focusedCamera = cameraName;
            var cameras = _mockForCameras.Cameras;
            int idx = 0;
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i].CameraId == cameraName) { idx = i; break; }
            }
            _currentCameraIndex = idx;

            UpdateCurrentCamera();

            // Show alert overlay
            AlertMessageText.Text = alertMessage.ToUpper();
            AlertOverlay.Visibility = Visibility.Visible;

            Debug.WriteLine($"Camera focus: {cameraName} - {alertMessage}");
        }

        private void ClearCameraFocus()
        {
            _focusedCamera = null;
            AlertOverlay.Visibility = Visibility.Collapsed;
            _rotationCountdown = 10;
            UpdateCurrentCamera();

            Debug.WriteLine("Camera focus cleared, resuming rotation");
        }

		#endregion

		#region Alert Filter Handlers - Removed (UI redesigned)
		// Old alert filter methods removed as UI was redesigned
		#endregion

		#region Alert Notification (Flash + Badge)

		private void OnAlertGeneratedForUI(object? sender, Station.Services.AlertGeneratedEventArgs e)
		{
			DispatcherQueue.TryEnqueue(() =>
			{
				// Thêm vào hàng đợi và xếp hạng ưu tiên (Khẩn cấp lên đầu)
				_pendingAlerts.Add(e.Alert);
				_pendingAlerts = _pendingAlerts.OrderByDescending(a => (int)a.Severity).ToList();
				_pendingAlertCount = _pendingAlerts.Count;

				// 1. Kích hoạt hiệu ứng UI (Badge)
				if (e.Alert.Severity != Station.Models.AlertSeverity.Low)
				{
					AlertBadgeCountText.Text = _pendingAlertCount.ToString();
					AlertNotificationBadge.Visibility = Visibility.Visible;
				}

				if (!string.IsNullOrEmpty(e.Alert.CameraId))
					FocusOnCameraAlert(e.Alert.CameraId, e.Alert.Title);

				// 2. BẮN LỆNH BẬT NHÁY XUỐNG BẢN ĐỒ
				if (_securityMapInitialized && SecurityMapWebView != null && e.Alert.NodeId != null)
				{
					var payload = new
					{
						type = "highlight-node",
						nodeId = e.Alert.NodeId,
						severity = e.Alert.Severity.ToString()
					};
					var json = JsonSerializer.Serialize(payload);
					SecurityMapWebView.CoreWebView2.PostWebMessageAsJson(json);
				}

				// 3. Nếu là lỗi Khẩn Cấp -> Ép bật Popup luôn
				if (e.Alert.Severity == Station.Models.AlertSeverity.Critical)
				{
					RedFlashStoryboard.Begin();
					if (!_alertDialogOpen)
					{
						AlertNotificationBadge_Click(null, null);
					}
				}
			});
		}

		private async void AlertNotificationBadge_Click(object sender, RoutedEventArgs e)
		{
			if (_alertDialogOpen || _pendingAlerts.Count == 0) return;
			_alertDialogOpen = true;

			RedFlashStoryboard.Stop();
			RedFlashOverlay.Opacity = 0;

			// Xử lý lần lượt từng cảnh báo trong hàng đợi
			while (_pendingAlerts.Count > 0)
			{
				var currentAlert = _pendingAlerts.First();

				// Mức Thấp (Low) không cần xem Popup
				if (currentAlert.Severity == Station.Models.AlertSeverity.Low)
				{
					_pendingAlerts.Remove(currentAlert);
					continue;
				}

				var dialog = new Station.Dialogs.AlertVideoDialog(currentAlert)
				{
					XamlRoot = this.XamlRoot
				};

				await dialog.ShowAsync();

				if (dialog.WasAcknowledged)
				{
					// Đã bấm Xác nhận -> Xóa khỏi hàng đợi
					_pendingAlerts.Remove(currentAlert);

					// BẮN LỆNH TẮT NHÁY, TRẢ VỀ XANH CHO BẢN ĐỒ
					if (_securityMapInitialized && SecurityMapWebView != null && currentAlert.NodeId != null)
					{
						var payload = new { type = "restore-node", nodeId = currentAlert.NodeId };
						var json = JsonSerializer.Serialize(payload);
						SecurityMapWebView.CoreWebView2.PostWebMessageAsJson(json);
					}
				}
				else
				{
					// Bấm Hủy/Đóng -> Tạm dừng xem, để lại trong Badge
					break;
				}
			}

			// Tính toán lại trạng thái Badge
			_pendingAlertCount = _pendingAlerts.Count;
			if (_pendingAlertCount == 0)
			{
				AlertNotificationBadge.Visibility = Visibility.Collapsed;
			}
			else
			{
				AlertBadgeCountText.Text = _pendingAlertCount.ToString();
				if (_pendingAlerts.Any(a => a.Severity == Station.Models.AlertSeverity.Critical))
				{
					RedFlashStoryboard.Begin();
				}
			}

			_alertDialogOpen = false;
		}

        #endregion

		private void AlertBadge_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Hand);
        }

        private void AlertBadge_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

      

        #region Join Request Notification Badge

        private void OnJoinRequestForUI(object? sender, Station.Services.JoinRequestNotification req)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _pendingJoinRequestCount++;
                JoinBadgeCountText.Text = _pendingJoinRequestCount.ToString();
                JoinRequestBadge.Visibility = Visibility.Visible;
            });
        }

        private void JoinRequestBadge_Click(object sender, RoutedEventArgs e)
        {
            _pendingJoinRequestCount = 0;
            JoinRequestBadge.Visibility = Visibility.Collapsed;
            OpenModuleWindow("Thiết bị", typeof(DevicesPage));
        }

        private void JoinBadge_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Hand);
        }

        private void JoinBadge_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        #endregion

        public class SystemLogItem
        {
            public string Icon { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Level { get; set; } = string.Empty;
            public string Time { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }

            public SolidColorBrush LevelBrush
            {
                get => Level switch
                {
                    "SUCCESS" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 34, 197, 94)), // #22C55E Green
                    "INFO" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 130, 246)), // #3B82F6 Blue
                    "WARNING" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 158, 11)), // #F59E0B Orange
                    "ERROR" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68)), // #EF4444 Red
                    "ALERT" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 179, 8)), // #EAB308 Yellow
                    _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184)) // #94A3B8 Gray
                };
            }
        }
    }
}
