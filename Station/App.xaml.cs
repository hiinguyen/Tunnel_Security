using DotNetEnv;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using Station.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Station
{
    public partial class App : Application
    {
        public Window? m_window { get; set; }
        private SimulationApiServer? _simServer;

        public App()
        {
            InitializeComponent();
            LoadEnv();

            // Initialize LiveCharts SkiaSharp renderer — required before any CartesianChart is created
            LiveCharts.Configure(config => config
                .AddSkiaSharp()
                .AddDefaultMappers());

            this.UnhandledException += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[UNHANDLED EXCEPTION] {e.Exception}");
                e.Handled = true;
            };
        }

        private static void LoadEnv()
        {
            // Load .env from the package/exe directory first (most reliable in MSIX packaging).
            // Fall back to TraversePath so local unpackaged runs still work.
            var exeEnv = Path.Combine(AppContext.BaseDirectory, ".env");
            if (File.Exists(exeEnv))
                Env.Load(exeEnv);
            else
                Env.TraversePath().Load();

            System.Diagnostics.Debug.WriteLine(
                $"[App] DATA_SOURCE={Environment.GetEnvironmentVariable("DATA_SOURCE") ?? "(not set → mock)"}");
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Khởi tạo data service theo DATA_SOURCE env var
            var dataService = DataServiceLocator.Initialize();
            dataService.Start();
            AlertNotificationService.Instance.Start();

            // Chạy simulation API server nếu dùng mock mode
            if (dataService is MockDataService)
            {
                _simServer = new SimulationApiServer();
                Task.Run(() => _simServer.StartAsync());
            }

            // Mở trang login trước; MainWindow được tạo sau khi đăng nhập thành công
            var loginWindow = new Microsoft.UI.Xaml.Window();
            var frame = new Microsoft.UI.Xaml.Controls.Frame();
            frame.Navigate(typeof(Station.Views.LoginPage));
            loginWindow.Content = frame;
            m_window = loginWindow;
            loginWindow.Activate();
        }
    }
}
