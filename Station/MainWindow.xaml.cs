using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Station.Services;
using Station.Views;

namespace Station
{
    public sealed partial class MainWindow : Window
    {
        private static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromMinutes(15);
        private readonly StationConfigService _configService;
        private readonly ThemeService _themeService;
        private readonly SessionIdleMonitor _idleMonitor;
        private readonly Dictionary<Type, Window> _openWindows = new();
        private bool _isSessionLocking;
        private bool _isClosed;

        public MainWindow()
        {
            InitializeComponent();
            _configService = new StationConfigService();
            _themeService = ThemeService.Instance;
            _idleMonitor = new SessionIdleMonitor(IdleSessionTimeout);
            Station.Controls.AlertFlashHost.Attach(this);

            Title = "Trạm Nghĩa Đô - Hệ thống giám sát xâm nhập";

            // Set window to maximized/fullscreen for 4K dashboard
            MaximizeWindow(this);

            // Subscribe to theme changes
            _themeService.ThemeChanged += OnThemeChanged;
            Closed += MainWindow_Closed;
            _idleMonitor.IdleTimeoutReached += IdleMonitor_IdleTimeoutReached;
            _idleMonitor.RegisterWindow(this);
            _idleMonitor.Start();

            // Apply current theme (default to Dark for 4K monitoring)
            _themeService.SetTheme(ElementTheme.Dark);
            ApplyTheme(ElementTheme.Dark);

            // Load station info
            _ = InitializeAsync();
        }

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            try
            {
                // Load station info if configured
                var config = await _configService.GetConfigAsync();
                if (config != null)
                {
                    Title = $"{config.StationName} - Hệ thống giám sát xâm nhập";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            }

            // Navigate to MonitoringDashboard in the main frame
            MonitoringFrame.Navigate(typeof(MonitoringDashboardPage));
        }

        /// <summary>
        /// Reload station configuration (called after saving new config)
        /// </summary>
        public async System.Threading.Tasks.Task ReloadStationConfigAsync()
        {
            try
            {
                var config = await _configService.GetConfigAsync();
                if (config != null)
                {
                    Title = $"{config.StationName} - Hệ thống giám sát xâm nhập";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reloading config: {ex.Message}");
            }
        }

        private void OnThemeChanged(object? sender, ElementTheme theme)
        {
            if (_isClosed)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyTheme(theme);
            });
        }

        private void ApplyTheme(ElementTheme theme)
        {
            if (_isClosed)
            {
                return;
            }

            try
            {
                if (Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = theme;
                }
            }
            catch (COMException)
            {
                return;
            }

            var invalidWindowTypes = new List<Type>();

            foreach (var entry in _openWindows)
            {
                try
                {
                    if (entry.Value.Content is FrameworkElement element)
                    {
                        element.RequestedTheme = theme;
                    }
                }
                catch (COMException)
                {
                    invalidWindowTypes.Add(entry.Key);
                }
            }

            foreach (var windowType in invalidWindowTypes)
            {
                _openWindows.Remove(windowType);
            }

            System.Diagnostics.Debug.WriteLine($"Theme changed to: {theme}");
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _isClosed = true;
            _themeService.ThemeChanged -= OnThemeChanged;
            _idleMonitor.IdleTimeoutReached -= IdleMonitor_IdleTimeoutReached;
            _idleMonitor.Dispose();
        }

        public void OpenPageInNewWindow<TPage>(string title) where TPage : Page, new()
        {
            var pageType = typeof(TPage);

            // Check if window is already open
            if (_openWindows.ContainsKey(pageType))
            {
                // Activate existing window
                _openWindows[pageType].Activate();
                return;
            }

            // Create new window
            var newWindow = new Window
            {
                Title = $"{title} - Trạm Nghĩa Đô",
                SystemBackdrop = new MicaBackdrop()
            };

            // Create frame with the page
            var frame = new Frame
            {
                Background = Application.Current.Resources["BackgroundSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush
            };

            frame.Navigate(pageType);
            newWindow.Content = frame;

            // Apply current theme
            if (frame is FrameworkElement element)
            {
                element.RequestedTheme = _themeService.CurrentTheme;
            }

            // Cửa sổ module cũng nháy đỏ khi có cảnh báo
            Station.Controls.AlertFlashHost.Attach(newWindow);

            // Set window to maximized/fullscreen
            MaximizeWindow(newWindow);

            // Handle window closed event
            newWindow.Closed += (s, e) =>
            {
                _idleMonitor.UnregisterWindow(newWindow);
                _openWindows.Remove(pageType);
                System.Diagnostics.Debug.WriteLine($"Closed window: {title}");
            };

            // Track the window
            _openWindows[pageType] = newWindow;
            _idleMonitor.RegisterWindow(newWindow);

            // Activate the window
            newWindow.Activate();

            System.Diagnostics.Debug.WriteLine($"Opened new window: {title}");
        }

        public async System.Threading.Tasks.Task LogoutAsync()
        {
            await CloseCurrentSessionAsync(null);
        }

        public async System.Threading.Tasks.Task LockSessionDueToInactivityAsync()
        {
            var minutes = Math.Max(1, (int)Math.Round(IdleSessionTimeout.TotalMinutes));
            await CloseCurrentSessionAsync($"Phiên làm việc đã tự khoá do không có thao tác trong {minutes} phút. Vui lòng đăng nhập lại để tiếp tục.");
        }

        private async System.Threading.Tasks.Task CloseCurrentSessionAsync(string? pendingMessage)
        {
            if (_isSessionLocking)
            {
                return;
            }

            _isSessionLocking = true;
            _idleMonitor.Stop();

            if (!string.IsNullOrWhiteSpace(pendingMessage))
            {
                SessionLockState.SetPendingMessage(pendingMessage);
            }

            AuthSession.SignOut();
            CurrentUserSession.Instance.Clear();

            foreach (var window in _openWindows.Values.ToList())
            {
                window.Close();
            }

            _openWindows.Clear();

            var loginWindow = new Window();
            var frame = new Frame();
            frame.Navigate(typeof(LoginPage));
            loginWindow.Content = frame;

            if (Application.Current is App app)
            {
                app.m_window = loginWindow;
            }

            MaximizeWindow(loginWindow);
            loginWindow.Activate();

            await System.Threading.Tasks.Task.Yield();
            Close();
        }

        private void IdleMonitor_IdleTimeoutReached(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await LockSessionDueToInactivityAsync();
            });
        }

        /// <summary>
        /// Maximize/Fullscreen a window
        /// </summary>
        private static void MaximizeWindow(Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
    }
}
