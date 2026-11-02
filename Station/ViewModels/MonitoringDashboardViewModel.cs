using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Station.Models;
using Station.Services;

namespace Station.ViewModels
{
    public partial class MonitoringDashboardViewModel : ObservableObject
    {
        [ObservableProperty] private string currentTime = DateTime.Now.ToString("HH:mm:ss");
        [ObservableProperty] private string currentDate = DateTime.Now.ToString("dd/MM/yyyy");
        [ObservableProperty] private int totalNodes;
        [ObservableProperty] private int activeNodes;
        [ObservableProperty] private int offlineNodes;
        [ObservableProperty] private int todayAlerts;
        [ObservableProperty] private int criticalAlerts;
        [ObservableProperty] private int warningAlerts;
        [ObservableProperty] private string systemStatus = "Hoạt động bình thường";
        [ObservableProperty] private string networkLatency = "12ms";
        [ObservableProperty] private double cpuUsage = 45.2;
        [ObservableProperty] private double memoryUsage = 68.7;

        // ── Chart series (set once, never reassigned) ─────────────────
        public ISeries[] AlertDistributionSeries { get; private set; } = Array.Empty<ISeries>();

        // ── Data monitoring: worst sensor per category (not a station-wide average) ──
        // Created in the constructor so the UI-thread brushes inside are built on the UI thread.
        public CategoryMonitor TempMonitor  { get; }
        public CategoryMonitor HumMonitor   { get; }
        public CategoryMonitor RadarMonitor { get; }
        public CategoryMonitor PirMonitor   { get; }

        // LiveCharts axes hold per-chart state, so each sparkline needs its own instances.
        // Bound via x:Bind function bindings: every call returns a fresh hidden axis.
        public System.Collections.Generic.IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> CreateHiddenXAxes() =>
            new LiveChartsCore.Kernel.Sketches.ICartesianAxis[] { new Axis { IsVisible = false, MinLimit = 0, MaxLimit = 23 } };

        public System.Collections.Generic.IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> CreateHiddenYAxes() =>
            new LiveChartsCore.Kernel.Sketches.ICartesianAxis[] { new Axis { IsVisible = false } };

        // Alert type counts
        [ObservableProperty] private int intruAlerts;
        [ObservableProperty] private int motionAlerts;
        [ObservableProperty] private int fireAlerts;
        [ObservableProperty] private int smokeAlerts;
        [ObservableProperty] private int waterAlerts;

        // Device counts
        [ObservableProperty] private int totalCameras;
        [ObservableProperty] private int onlineCameras;
        [ObservableProperty] private int totalSensors;
        [ObservableProperty] private int onlineSensors;
        [ObservableProperty] private double systemHealth;
        [ObservableProperty] private string systemHealthText = "0%";
        [ObservableProperty] private string cameraCountText = "0 / 0";
        [ObservableProperty] private string sensorCountText = "0 / 0";
        [ObservableProperty] private int anomalyCount;
        [ObservableProperty] private int activeAlertCount;

        // Analytics panel KPIs (derived from alert history, not shown by the status panel)
        [ObservableProperty] private int resolvedAlerts;
        [ObservableProperty] private string resolvedRateText = "-";
        [ObservableProperty] private string avgHandleTimeText = "-";

        public ObservableCollection<NodeStatus>         Nodes               { get; } = new();
        public ObservableCollection<RealtimeAlert>      RealtimeAlerts      { get; } = new();
        public ObservableCollection<TemperatureReading> TemperatureReadings { get; } = new();
        public ObservableCollection<CameraStatus>       Cameras             { get; } = new();

        // ── FIX 2: Persistent ObservableValues for pie chart ──────────────
        // PieSeries[] is created once; only .Value is mutated on each alert.
        private readonly ObservableValue _pieIntru       = new(0);
        private readonly ObservableValue _pieMotion      = new(0);
        private readonly ObservableValue _pieFire        = new(0);
        private readonly ObservableValue _pieSmoke       = new(0);
        private readonly ObservableValue _piePlaceholder = new(1);

        private readonly IDataService _mock = DataServiceLocator.Current;
        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly Random _rng = new();

        public MonitoringDashboardViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            // Sensor identity tints (match MonitoringSensor*Brush, dark theme) - never severity colors.
            TempMonitor  = new CategoryMonitor(AlertCategory.Temperature, new SKColor(0xFF, 0xB3, 0x47), "F1");
            HumMonitor   = new CategoryMonitor(AlertCategory.Humidity,    new SKColor(0x22, 0xD3, 0xEE), "F0");
            RadarMonitor = new CategoryMonitor(AlertCategory.Radar,       new SKColor(0x81, 0x8C, 0xF8), "F0");
            PirMonitor   = new CategoryMonitor(AlertCategory.Infrared,    new SKColor(0x84, 0xCC, 0x16), "F0");

            InitializePieChart();
            LoadFromMockData();
            RefreshAlertCounts();
            SyncPieValues();   // sync pie to initial alert counts

            _mock.SensorTick    += OnSensorTick;
            _mock.AlertGenerated += OnAlertGenerated;

            StartClockUpdates();
        }

        // ── Load initial state ────────────────────────────────────────

        private void LoadFromMockData()
        {
            var sensors  = _mock.Sensors;
            var cameras  = _mock.Cameras;
            var allNodes = _mock.Lines.SelectMany(l => l.Nodes).ToList();

            TotalNodes   = allNodes.Count;
            ActiveNodes  = allNodes.Count(n => sensors.Any(s => s.NodeId == n.NodeId && s.IsOnline));
            OfflineNodes = allNodes.Count(n => sensors.All(s => s.NodeId == n.NodeId && !s.IsOnline));

            TotalCameras  = cameras.Count;
            OnlineCameras = cameras.Count(c => c.IsOnline);
            TotalSensors  = sensors.Count;
            OnlineSensors = sensors.Count(s => s.IsOnline);

            int totalDevices = TotalCameras + TotalSensors;
            SystemHealth     = totalDevices > 0
                ? Math.Round((OnlineCameras + OnlineSensors) * 100.0 / totalDevices, 0)
                : 0;
            SystemHealthText = $"{SystemHealth:F0}%";
            CameraCountText  = $"{OnlineCameras} / {TotalCameras}";
            SensorCountText  = $"{OnlineSensors} / {TotalSensors}";
            AnomalyCount     = sensors.Count(s => s.CurrentLevel >= SensorAlertLevel.Warning);
            ActiveAlertCount = _mock.ActiveAlerts.Count;

            var tempSensors = sensors.Where(s => s.Category == AlertCategory.Temperature).ToList();

            TempMonitor.Refresh(sensors);
            HumMonitor.Refresh(sensors);
            RadarMonitor.Refresh(sensors);
            PirMonitor.Refresh(sensors);

            Nodes.Clear();
            foreach (var node in allNodes)
            {
                var nodeSensors = sensors.Where(s => s.NodeId == node.NodeId).ToList();
                var tempSensor  = nodeSensors.FirstOrDefault(s => s.Category == AlertCategory.Temperature);
                Nodes.Add(new NodeStatus
                {
                    NodeId      = node.NodeId,
                    Location    = node.NodeName,
                    IsOnline    = nodeSensors.Any(s => s.IsOnline),
                    Temperature = tempSensor != null ? Math.Round(tempSensor.CurrentValue, 1) : 0,
                    LastUpdate  = DateTime.Now
                });
            }

            Cameras.Clear();
            foreach (var cam in cameras)
            {
                Cameras.Add(new CameraStatus
                {
                    CameraId    = cam.CameraId,
                    Location    = cam.Location,
                    IsRecording = cam.IsOnline,
                    Fps         = 25,
                    Resolution  = "1920x1080"
                });
            }

            TemperatureReadings.Clear();
            double baseTemp = tempSensors.Count > 0 ? tempSensors.Average(s => s.NominalValue) : 24.0;
            for (int i = 0; i < 24; i++)
                TemperatureReadings.Add(new TemperatureReading { Hour = i, Temperature = baseTemp });

            RealtimeAlerts.Clear();
            foreach (var alert in _mock.AlertHistory.Take(10))
            {
                RealtimeAlerts.Add(new RealtimeAlert
                {
                    Source    = alert.NodeId,
                    Message   = alert.Title,
                    Timestamp = alert.CreatedAt.DateTime,
                    Severity  = SeverityToString(alert.Severity)
                });
            }
        }

        private void RefreshAlertCounts()
        {
            var history = _mock.AlertHistory;
            TodayAlerts    = history.Count;
            CriticalAlerts = history.Count(a => a.Severity == AlertSeverity.Critical);
            WarningAlerts  = history.Count(a => a.Severity == AlertSeverity.High || a.Severity == AlertSeverity.Medium);
            IntruAlerts    = history.Count(a => a.Severity == AlertSeverity.Low);
            MotionAlerts   = history.Count(a => a.Severity == AlertSeverity.Medium);
            FireAlerts     = history.Count(a => a.Severity == AlertSeverity.High);
            SmokeAlerts    = history.Count(a => a.Severity == AlertSeverity.Critical);
            WaterAlerts    = 0;

            var resolved = history
                .Where(a => a.State == AlertState.Resolved || a.State == AlertState.Closed)
                .ToList();
            ResolvedAlerts   = resolved.Count;
            ResolvedRateText = history.Count == 0 ? "-" : $"{resolved.Count * 100.0 / history.Count:0}%";

            var handled = resolved
                .Where(a => (a.ResolvedAt ?? a.ClosedAt).HasValue)
                .Select(a => (a.ResolvedAt ?? a.ClosedAt)!.Value - a.CreatedAt)
                .Where(t => t >= TimeSpan.Zero)
                .ToList();
            AvgHandleTimeText = handled.Count == 0
                ? "-"
                : FormatDuration(TimeSpan.FromTicks((long)handled.Average(t => t.Ticks)));
        }

        private static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours} giờ {t.Minutes} phút" : $"{Math.Max(1, (int)t.TotalMinutes)} phút";

        // ── Live event handlers ───────────────────────────────────────

        private void OnSensorTick(object? sender, SensorTickEventArgs e)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                var sensors = _mock.Sensors;
                switch (e.Sensor.Category)
                {
                    case AlertCategory.Temperature:
                        TempMonitor.OnTick(sensors, e.Sensor, e.NewValue);
                        var node = Nodes.FirstOrDefault(n => n.NodeId == e.Sensor.NodeId);
                        if (node != null)
                        {
                            node.Temperature = Math.Round(e.NewValue, 1);
                            node.LastUpdate  = DateTime.Now;
                        }
                        int hour = DateTime.Now.Hour;
                        if (hour < TemperatureReadings.Count)
                            TemperatureReadings[hour].Temperature = Math.Round(e.NewValue, 1);
                        break;

                    case AlertCategory.Humidity:
                        HumMonitor.OnTick(sensors, e.Sensor, e.NewValue);
                        break;

                    case AlertCategory.Radar:
                        RadarMonitor.OnTick(sensors, e.Sensor, e.NewValue);
                        break;

                    case AlertCategory.Infrared:
                        PirMonitor.OnTick(sensors, e.Sensor, e.NewValue);
                        break;
                }
            });
        }

        private void OnAlertGenerated(object? sender, AlertGeneratedEventArgs e)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                RefreshAlertCounts();
                AnomalyCount     = _mock.Sensors.Count(s => s.CurrentLevel >= SensorAlertLevel.Warning);
                ActiveAlertCount = _mock.ActiveAlerts.Count;

                // FIX 2: mutate ObservableValues in-place — no new array, no full re-render
                SyncPieValues();

                RealtimeAlerts.Insert(0, new RealtimeAlert
                {
                    Source    = e.Alert.NodeId,
                    Message   = e.Alert.Title,
                    Timestamp = e.Alert.CreatedAt.DateTime,
                    Severity  = SeverityToString(e.Alert.Severity)
                });
                if (RealtimeAlerts.Count > 10)
                    RealtimeAlerts.RemoveAt(RealtimeAlerts.Count - 1);

                SystemStatus = CriticalAlerts > 0 ? "Có cảnh báo khẩn cấp" : "Hoạt động bình thường";
            });
        }

        // ── Clock + system metrics ────────────────────────────────────

        private async void StartClockUpdates()
        {
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(2000);
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    CurrentTime    = DateTime.Now.ToString("HH:mm:ss");
                    CurrentDate    = DateTime.Now.ToString("dd/MM/yyyy");
                    CpuUsage       = Math.Round(40 + _rng.NextDouble() * 20, 1);
                    MemoryUsage    = Math.Round(65 + _rng.NextDouble() * 10, 1);
                    NetworkLatency = $"{10 + _rng.Next(10)}ms";
                });
            }
        }

        // ── Chart builders ────────────────────────────────────────────

        private void InitializePieChart()
        {
            // FIX 2: Create persistent pie series ONCE — each wraps its ObservableValue.
            // On each alert, only .Value is mutated → LiveCharts animates the diff.
            AlertDistributionSeries = new ISeries[]
            {
                MakePieSeries("Thấp",              _pieIntru,       new SKColor(34, 197, 94)),
                MakePieSeries("Trung bình",         _pieMotion,      new SKColor(234, 179, 8)),
                MakePieSeries("Cao",                _pieFire,        new SKColor(249, 115, 22)),
                MakePieSeries("Nghiêm trọng",       _pieSmoke,       new SKColor(239, 68, 68)),
                MakePieSeries("Không có cảnh báo",  _piePlaceholder, new SKColor(75, 85, 99), showLabel: false),
            };
        }

        private void SyncPieValues()
        {
            bool hasAny = IntruAlerts > 0 || MotionAlerts > 0 || FireAlerts > 0 || SmokeAlerts > 0;
            _pieIntru.Value       = IntruAlerts;
            _pieMotion.Value      = MotionAlerts;
            _pieFire.Value        = FireAlerts;
            _pieSmoke.Value       = SmokeAlerts;
            // Placeholder visible only when all slices are zero
            _piePlaceholder.Value = hasAny ? 0 : 1;
        }

        private static PieSeries<ObservableValue> MakePieSeries(string name, ObservableValue value, SKColor color, bool showLabel = true) =>
            new()
            {
                Name                 = name,
                Values               = new[] { value },
                Fill                 = new SolidColorPaint(color),
                DataLabelsPaint      = new SolidColorPaint(new SKColor(255, 255, 255)),
                DataLabelsSize       = 16,
                DataLabelsPosition   = LiveChartsCore.Measure.PolarLabelsPosition.Middle,
                // Hide labels on empty slices (value 0) and on the placeholder ring.
                DataLabelsFormatter  = point => showLabel && point.Coordinate.PrimaryValue > 0
                                                    ? $"{point.Coordinate.PrimaryValue:0}"
                                                    : string.Empty,
                HoverPushout         = 15,
                MaxRadialColumnWidth = double.MaxValue,
            };

        // ── Helpers ───────────────────────────────────────────────────

        private static string SeverityToString(AlertSeverity severity) => severity switch
        {
            AlertSeverity.Critical => "critical",
            AlertSeverity.High     => "high",
            AlertSeverity.Medium   => "warning",
            _                      => "low"
        };
    }

    public class NodeStatus
    {
        public string NodeId    { get; set; } = string.Empty;
        public string Location  { get; set; } = string.Empty;
        public bool   IsOnline  { get; set; }
        public double Temperature { get; set; }
        public DateTime LastUpdate { get; set; }

        public SolidColorBrush StatusBrush => IsOnline
            ? new SolidColorBrush(Color.FromArgb(255, 63, 207, 142))
            : new SolidColorBrush(Color.FromArgb(255, 123, 126, 133));

        public SolidColorBrush TemperatureBrush => Temperature > 35
            ? new SolidColorBrush(Color.FromArgb(255, 240, 98, 93))
            : new SolidColorBrush(Color.FromArgb(255, 154, 166, 178));

        public string StatusText      => IsOnline ? "Online" : "Offline";
        public string TemperatureText => $"{Temperature:F1}°C";
        public string LastUpdateText  => $"{(DateTime.Now - LastUpdate).TotalMinutes:F0}m trước";
    }

    public class RealtimeAlert
    {
        public string   Source    { get; set; } = string.Empty;
        public string   Message   { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string   Severity  { get; set; } = "low";

        public string TimeText => Timestamp.ToString("HH:mm:ss");

        public SolidColorBrush SeverityBrush => Severity switch
        {
            "critical" => new SolidColorBrush(Color.FromArgb(255, 240, 98, 93)),
            "high"     => new SolidColorBrush(Color.FromArgb(255, 255, 209, 102)),
            "warning"  => new SolidColorBrush(Color.FromArgb(255, 255, 209, 102)),
            _          => new SolidColorBrush(Color.FromArgb(255, 154, 166, 178))
        };

        public string SeverityLabel => Severity switch
        {
            "critical" => "KHẨN CẤP",
            "high"     => "CAO",
            "warning"  => "TRUNG BÌNH",
            _          => "THẤP"
        };
    }

    public class TemperatureReading
    {
        public int    Hour        { get; set; }
        public double Temperature { get; set; }
        public string HourLabel   => $"{Hour:D2}:00";
    }

    public class CameraStatus
    {
        public string CameraId   { get; set; } = string.Empty;
        public string Location   { get; set; } = string.Empty;
        public bool   IsRecording { get; set; }
        public int    Fps         { get; set; }
        public string Resolution  { get; set; } = string.Empty;

        public SolidColorBrush StatusBrush => IsRecording
            ? new SolidColorBrush(Color.FromArgb(255, 63, 207, 142))
            : new SolidColorBrush(Color.FromArgb(255, 123, 126, 133));

        public string StatusText => IsRecording ? $"Recording • {Fps} FPS" : "Offline";
    }
}
