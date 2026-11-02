using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Station.Models;
using Station.Services;
using Windows.UI;

namespace Station.Views
{
    public sealed partial class AnalyticsReportPage : Page
    {
        private const string AllPeriods = "Tất cả thời gian";
        private const string TodayPeriod = "Hôm nay";
        private const string WeekPeriod = "7 ngày qua";
        private const string MonthPeriod = "30 ngày qua";
        private const string AllLines = "Tất cả tuyến";
        private const string AllNodes = "Tất cả nút";
        private const string AllCameras = "Tất cả camera";
        private const string AllSeverities = "Tất cả mức độ";
        private const string AllStatuses = "Tất cả trạng thái";

        private readonly MockDataService _mock = MockDataService.Instance;
        private readonly List<AlertHistoryRecord> _allRecords = new();
        private readonly Random _realtimeRandom = new(20260604);
        private readonly ThemeService _themeService = ThemeService.Instance;
        private int _realtimeSequence;
        private bool _updatingFilters;

        public ObservableCollection<TopNodeStat> TopNodes { get; } = new();
        public ObservableCollection<AlertHistoryRecord> History { get; } = new();
        public ObservableCollection<HeatmapCell> HeatmapCells { get; } = new();
        public ObservableCollection<HourBucketStat> HeatmapHourBars { get; } = new();
        public ObservableCollection<DayBucketStat> HeatmapDayBars { get; } = new();
        public ObservableCollection<string> HeatmapHourLabels { get; } = new();

        public ObservableCollection<string> PeriodOptions { get; } = new();
        public ObservableCollection<string> LineOptions { get; } = new();
        public ObservableCollection<string> NodeOptions { get; } = new();
        public ObservableCollection<string> CameraOptions { get; } = new();
        public ObservableCollection<string> SeverityOptions { get; } = new();
        public ObservableCollection<string> StatusOptions { get; } = new();

        public IEnumerable<ISeries> AlertsByLineSeries { get; set; } = Array.Empty<ISeries>();
        public IEnumerable<ICartesianAxis> LineAxes { get; set; } = Array.Empty<ICartesianAxis>();
        public IEnumerable<ICartesianAxis> LineYAxes { get; set; } = Array.Empty<ICartesianAxis>();

        public IEnumerable<ISeries> AlertsByHourSeries { get; set; } = Array.Empty<ISeries>();
        public IEnumerable<ICartesianAxis> HourAxes { get; set; } = Array.Empty<ICartesianAxis>();
        public IEnumerable<ICartesianAxis> HourYAxes { get; set; } = Array.Empty<ICartesianAxis>();

        public IEnumerable<ISeries> OverviewTrendSeries { get; set; } = Array.Empty<ISeries>();
        public IEnumerable<ICartesianAxis> OverviewTrendAxes { get; set; } = Array.Empty<ICartesianAxis>();
        public IEnumerable<ICartesianAxis> OverviewTrendYAxes { get; set; } = Array.Empty<ICartesianAxis>();

        public IEnumerable<ISeries> RealtimeTrendSeries { get; set; } = Array.Empty<ISeries>();
        public IEnumerable<ICartesianAxis> RealtimeAxes { get; set; } = Array.Empty<ICartesianAxis>();
        public IEnumerable<ICartesianAxis> RealtimeYAxes { get; set; } = Array.Empty<ICartesianAxis>();

        public IEnumerable<ISeries> SeverityDonutSeries { get; set; } = Array.Empty<ISeries>();
        public ObservableCollection<TopSourceStat> TopSources { get; } = new();

        public AnalyticsReportPage()
        {
            InitializeComponent();

            TopSourceRepeater.ItemsSource = TopSources;
            HistoryGrid.ItemsSource = History;
            HeatmapRepeater.ItemsSource = HeatmapCells;
            HeatmapHourBarsRepeater.ItemsSource = HeatmapHourBars;
            HeatmapDayBarsRepeater.ItemsSource = HeatmapDayBars;
            HeatmapHourLabelsRepeater.ItemsSource = HeatmapHourLabels;

            InitializeFilters();
            LoadAnalyticsRecords();
            SeedRealtimeMockSnapshot();
            RefreshFilterOptions();
            RequestedTheme = _themeService.CurrentTheme;
            ApplyAnalytics();
            UpdateThemeIcons();

            _themeService.ThemeChanged += ThemeService_ThemeChanged;
            Unloaded += AnalyticsReportPage_Unloaded;
        }

        private void ThemeService_ThemeChanged(object? sender, ElementTheme theme)
        {
            RequestedTheme = theme;
            UpdateThemeIcons();
            ApplyAnalytics();
        }

        private void AnalyticsReportPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _themeService.ThemeChanged -= ThemeService_ThemeChanged;
        }

        private void SeedRealtimeMockSnapshot()
        {
            var now = DateTimeOffset.Now;
            for (var i = 11; i >= 0; i--)
            {
                _allRecords.Insert(0, BuildRealtimeRecord(now.AddMinutes(-i)));
            }

            if (_allRecords.Count > 450)
            {
                _allRecords.RemoveRange(450, _allRecords.Count - 450);
            }
        }

        private AlertHistoryRecord BuildRealtimeRecord(DateTimeOffset timestamp)
        {
            var locations = BuildLocations();
            var location = locations[_realtimeRandom.Next(locations.Count)];
            var categories = new[] { "Nhiệt độ vượt ngưỡng", "Rung động bất thường", "Độ ẩm vượt ngưỡng", "Mực nước tăng nhanh", "Mất kết nối camera", "Phát hiện xâm nhập" };
            var statuses = new[] { "Chờ xử lý", "Đang xử lý", "Đã xác nhận", "Đã đóng" };
            var severityPool = new[] { ("Thấp", 1), ("Trung bình", 2), ("Cao", 3), ("Nghiêm trọng", 4) };
            var severity = severityPool[_realtimeRandom.Next(severityPool.Length)];
            var status = statuses[_realtimeRandom.Next(statuses.Length)];

            _realtimeSequence++;

            return new AlertHistoryRecord
            {
                CreatedAt = timestamp,
                Timestamp = timestamp.ToString("HH:mm:ss dd/MM/yyyy"),
                Line = ShortLineName(location.LineName),
                LineName = location.LineName,
                Node = location.NodeName,
                Camera = location.CameraId,
                AlertType = categories[(_realtimeSequence + _realtimeRandom.Next(categories.Length)) % categories.Length],
                Severity = severity.Item1,
                SeverityWeight = severity.Item2,
                Status = status,
                IsOpen = status != "Đã đóng",
                IsRealtime = true
            };
        }

        private void InitializeFilters()
        {
            PeriodOptions.Add(TodayPeriod);
            PeriodOptions.Add(WeekPeriod);
            PeriodOptions.Add(MonthPeriod);
            PeriodOptions.Add(AllPeriods);

            SeverityOptions.Add(AllSeverities);
            SeverityOptions.Add("Nghiêm trọng");
            SeverityOptions.Add("Cao");
            SeverityOptions.Add("Trung bình");
            SeverityOptions.Add("Thấp");

            StatusOptions.Add(AllStatuses);
            StatusOptions.Add("Chờ xử lý");
            StatusOptions.Add("Đã xác nhận");
            StatusOptions.Add("Đang xử lý");
            StatusOptions.Add("Đã đóng");

            PeriodFilterComboBox.ItemsSource = PeriodOptions;
            LineFilterComboBox.ItemsSource = LineOptions;
            NodeFilterComboBox.ItemsSource = NodeOptions;
            CameraFilterComboBox.ItemsSource = CameraOptions;
            SeverityFilterComboBox.ItemsSource = SeverityOptions;
            StatusFilterComboBox.ItemsSource = StatusOptions;

            _updatingFilters = true;
            PeriodFilterComboBox.SelectedItem = WeekPeriod;
            SeverityFilterComboBox.SelectedItem = AllSeverities;
            StatusFilterComboBox.SelectedItem = AllStatuses;
            _updatingFilters = false;
        }

        private void LoadAnalyticsRecords()
        {
            _allRecords.Clear();

            var sourceAlerts = _mock.AlertHistory
                .OrderByDescending(a => a.CreatedAt)
                .Select(MapAlert)
                .ToList();

            if (sourceAlerts.Count > 0)
            {
                _allRecords.AddRange(sourceAlerts);
                return;
            }

            _allRecords.AddRange(BuildFallbackRecords());
        }

        private AlertHistoryRecord MapAlert(Alert alert)
        {
            var cameraId = alert.CameraId;
            if (string.IsNullOrWhiteSpace(cameraId))
            {
                cameraId = _mock.Cameras.FirstOrDefault(c => c.NodeId == alert.NodeId)?.CameraId ?? "-";
            }

            return new AlertHistoryRecord
            {
                CreatedAt = alert.CreatedAt,
                Timestamp = alert.CreatedAt.ToString("HH:mm dd/MM/yyyy"),
                Line = ShortLineName(alert.LineName),
                LineName = alert.LineName,
                Node = string.IsNullOrWhiteSpace(alert.NodeName) ? alert.NodeId : alert.NodeName,
                Camera = cameraId,
                AlertType = CategoryToText(alert.Category),
                Severity = SeverityToText(alert.Severity),
                SeverityWeight = SeverityWeight(alert.Severity),
                Status = StateToText(alert.State),
                IsOpen = alert.State is AlertState.Unprocessed or AlertState.Acknowledged or AlertState.InProgress
            };
        }

        private IEnumerable<AlertHistoryRecord> BuildFallbackRecords()
        {
            var rng = new Random(41);
            var locations = BuildLocations();
            var categories = new[] { "Nhiệt độ vượt ngưỡng", "Rung động bất thường", "Độ ẩm vượt ngưỡng", "Mực nước tăng nhanh", "Mất kết nối camera", "Phát hiện xâm nhập" };
            var statuses = new[] { "Chờ xử lý", "Đang xử lý", "Đã xác nhận", "Đã đóng" };
            var severities = new[] { ("Thấp", 1), ("Trung bình", 2), ("Cao", 3), ("Nghiêm trọng", 4) };
            var records = new List<AlertHistoryRecord>();
            var now = DateTimeOffset.Now;

            for (var dayOffset = 0; dayOffset < 14; dayOffset++)
            {
                var alertCount = dayOffset < 7 ? rng.Next(6, 12) : rng.Next(3, 8);

                for (var i = 0; i < alertCount; i++)
                {
                    var location = locations[(i + rng.Next(locations.Count)) % locations.Count];
                    var hour = PickOperationalHour(rng);
                    var createdAt = now.Date.AddDays(-dayOffset)
                        .AddHours(hour)
                        .AddMinutes(rng.Next(0, 60));
                    var severity = severities[rng.Next(severities.Length)];
                    var status = statuses[rng.Next(statuses.Length)];

                    records.Add(new AlertHistoryRecord
                    {
                        CreatedAt = createdAt,
                        Timestamp = createdAt.ToString("HH:mm dd/MM/yyyy"),
                        Line = ShortLineName(location.LineName),
                        LineName = location.LineName,
                        Node = location.NodeName,
                        Camera = location.CameraId,
                        AlertType = categories[rng.Next(categories.Length)],
                        Severity = severity.Item1,
                        SeverityWeight = severity.Item2,
                        Status = status,
                        IsOpen = status != "Đã đóng"
                    });
                }
            }

            return records.OrderByDescending(r => r.CreatedAt);
        }

        private List<LocationInfo> BuildLocations()
        {
            var locations = new List<LocationInfo>();

            foreach (var line in _mock.Lines)
            {
                foreach (var node in line.Nodes)
                {
                    var camera = _mock.Cameras.FirstOrDefault(c => c.NodeId == node.NodeId);
                    locations.Add(new LocationInfo(
                        line.LineName,
                        node.NodeName,
                        camera?.CameraId ?? $"CAM-{node.NodeId}"));
                }
            }

            if (locations.Count > 0)
                return locations;

            return new List<LocationInfo>
            {
                new("Tuyến A1", "SEN-002", "CAM-A1-02"),
                new("Tuyến A2", "SEN-008", "CAM-A2-08"),
                new("Tuyến B1", "SEN-018", "CAM-B1-18"),
                new("Tuyến B2", "SEN-010", "CAM-B2-10"),
                new("Tuyến C1", "SEN-030", "CAM-C1-30"),
                new("Tuyến C2", "SEN-034", "CAM-C2-34")
            };
        }

        private static int PickOperationalHour(Random rng)
        {
            var bucket = rng.NextDouble();
            if (bucket < 0.42) return rng.Next(16, 21);
            if (bucket < 0.72) return rng.Next(6, 10);
            return rng.Next(0, 24);
        }

        private void RefreshFilterOptions()
        {
            _updatingFilters = true;

            ReplaceOptions(LineOptions, new[] { AllLines }.Concat(_allRecords.Select(r => r.LineName).Distinct().OrderBy(x => x)));
            LineFilterComboBox.SelectedItem ??= AllLines;

            RefreshNodeOptions();
            RefreshCameraOptions();

            _updatingFilters = false;
        }

        private void RefreshNodeOptions()
        {
            var selectedLine = SelectedText(LineFilterComboBox, AllLines);
            var records = _allRecords.AsEnumerable();

            if (selectedLine != AllLines)
                records = records.Where(r => r.LineName == selectedLine);

            var previous = SelectedText(NodeFilterComboBox, AllNodes);
            ReplaceOptions(NodeOptions, new[] { AllNodes }.Concat(records.Select(r => r.Node).Distinct().OrderBy(x => x)));
            NodeFilterComboBox.SelectedItem = NodeOptions.Contains(previous) ? previous : AllNodes;
        }

        private void RefreshCameraOptions()
        {
            var selectedLine = SelectedText(LineFilterComboBox, AllLines);
            var selectedNode = SelectedText(NodeFilterComboBox, AllNodes);
            var records = _allRecords.AsEnumerable();

            if (selectedLine != AllLines)
                records = records.Where(r => r.LineName == selectedLine);
            if (selectedNode != AllNodes)
                records = records.Where(r => r.Node == selectedNode);

            var previous = SelectedText(CameraFilterComboBox, AllCameras);
            ReplaceOptions(CameraOptions, new[] { AllCameras }.Concat(records.Select(r => r.Camera).Distinct().OrderBy(x => x)));
            CameraFilterComboBox.SelectedItem = CameraOptions.Contains(previous) ? previous : AllCameras;
        }

        private static void ReplaceOptions(ObservableCollection<string> target, IEnumerable<string> values)
        {
            target.Clear();
            foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct())
                target.Add(value);
        }

        private void ApplyAnalytics()
        {
            var filtered = ApplyFilters(_allRecords).ToList();
            var previous = ApplyPreviousPeriodFilter(_allRecords).ToList();

            UpdateKpis(filtered, previous);
            UpdateCharts(filtered);
            UpdateHeatmap(filtered);
            UpdateTopNodes(filtered);
            UpdateHistory(filtered);

            Bindings.Update();
        }

        private IEnumerable<AlertHistoryRecord> ApplyFilters(IEnumerable<AlertHistoryRecord> source)
        {
            var selectedPeriod = SelectedText(PeriodFilterComboBox, WeekPeriod);
            var selectedLine = SelectedText(LineFilterComboBox, AllLines);
            var selectedNode = SelectedText(NodeFilterComboBox, AllNodes);
            var selectedCamera = SelectedText(CameraFilterComboBox, AllCameras);
            var selectedSeverity = SelectedText(SeverityFilterComboBox, AllSeverities);
            var selectedStatus = SelectedText(StatusFilterComboBox, AllStatuses);
            var now = DateTimeOffset.Now;

            var records = selectedPeriod switch
            {
                TodayPeriod => source.Where(r => r.CreatedAt.Date == now.Date),
                WeekPeriod => source.Where(r => r.CreatedAt >= now.AddDays(-7)),
                MonthPeriod => source.Where(r => r.CreatedAt >= now.AddDays(-30)),
                _ => source
            };

            if (selectedLine != AllLines)
                records = records.Where(r => r.LineName == selectedLine);
            if (selectedNode != AllNodes)
                records = records.Where(r => r.Node == selectedNode);
            if (selectedCamera != AllCameras)
                records = records.Where(r => r.Camera == selectedCamera);
            if (selectedSeverity != AllSeverities)
                records = records.Where(r => r.Severity == selectedSeverity);
            if (selectedStatus != AllStatuses)
                records = records.Where(r => r.Status == selectedStatus);

            return records.OrderByDescending(r => r.CreatedAt);
        }

        private IEnumerable<AlertHistoryRecord> ApplyPreviousPeriodFilter(IEnumerable<AlertHistoryRecord> source)
        {
            var selectedPeriod = SelectedText(PeriodFilterComboBox, WeekPeriod);
            var now = DateTimeOffset.Now;

            return selectedPeriod switch
            {
                TodayPeriod => source.Where(r => r.CreatedAt.Date == now.AddDays(-1).Date),
                WeekPeriod => source.Where(r => r.CreatedAt >= now.AddDays(-14) && r.CreatedAt < now.AddDays(-7)),
                MonthPeriod => source.Where(r => r.CreatedAt >= now.AddDays(-60) && r.CreatedAt < now.AddDays(-30)),
                _ => Enumerable.Empty<AlertHistoryRecord>()
            };
        }

        private void UpdateKpis(IReadOnlyCollection<AlertHistoryRecord> filtered, IReadOnlyCollection<AlertHistoryRecord> previous)
        {
            var severeCount = filtered.Count(r => r.Severity is "Cao" or "Nghiêm trọng");
            var openCount = filtered.Count(r => r.IsOpen);
            var closedCount = filtered.Count - openCount;
            var lineCount = filtered.Select(r => r.LineName).Distinct().Count();
            var change = FormatChange(filtered.Count, previous.Count);
            var topNode = filtered
                .GroupBy(r => new { r.Node, r.LineName })
                .Select(g => new { g.Key.Node, g.Key.LineName, Count = g.Count(), Risk = g.Sum(x => x.SeverityWeight) })
                .OrderByDescending(g => g.Risk)
                .ThenByDescending(g => g.Count)
                .FirstOrDefault();

            TotalAlertsValue.Text = filtered.Count.ToString();
            TotalAlertsChange.Text = $"{change} so với kỳ trước";
            TotalAlertsChange.Foreground = BrushForChange(filtered.Count - previous.Count);
            ProcessedSummaryText.Text = $"Đã xử lý: {closedCount} ({Percent(closedCount, filtered.Count)})";
            OpenSummaryText.Text = $"Đang mở: {openCount} ({Percent(openCount, filtered.Count)})";

            SevereAlertsValue.Text = severeCount.ToString();
            SevereAlertsChange.Text = $"{severeCount - previous.Count(r => r.Severity is "Cao" or "Nghiêm trọng"):+#;-#;0} so với kỳ trước";
            SevereAlertsChange.Foreground = BrushForChange(severeCount - previous.Count(r => r.Severity is "Cao" or "Nghiêm trọng"));
            LineCountText.Text = $"Số tuyến có cảnh báo: {lineCount}";

            HotspotNodeText.Text = topNode?.Node ?? "-";
            HotspotNodeDetailText.Text = topNode == null
                ? "Chưa có dữ liệu"
                : $"{topNode.Count} cảnh báo · {topNode.LineName}";

            var onlineDevices = _mock.Sensors.Count(s => s.IsOnline) + _mock.Cameras.Count(c => c.IsOnline);
            var totalDevices = _mock.Sensors.Count + _mock.Cameras.Count;
            OnlineDeviceText.Text = $"{onlineDevices} / {Math.Max(totalDevices, onlineDevices)}";
            AvgHandleTimeText.Text = $"Thời gian xử lý TB: {EstimateAverageHandleTime(filtered)}";
            RealtimeSampleText.Text = $"Snapshot: {_allRecords.Count(r => r.IsRealtime)} mẫu mock";
            RealtimeStatusText.Text = $"SNAPSHOT · {DateTimeOffset.Now:HH:mm:ss}";
            RealtimeWindowText.Text = $"12 phút gần nhất · {filtered.Count(r => r.CreatedAt >= DateTimeOffset.Now.AddMinutes(-12))} cảnh báo được ghi nhận";
        }

        private void UpdateCharts(IReadOnlyCollection<AlertHistoryRecord> filtered)
        {
            var axisLabelColor = IsDarkThemeActive()
                ? new SKColor(226, 232, 240)
                : new SKColor(51, 65, 85);
            var axisGridColor = IsDarkThemeActive()
                ? new SKColor(75, 85, 99)
                : new SKColor(203, 213, 225);

            UpdateDashboardOverviewCharts(filtered, axisLabelColor, axisGridColor);

            var byLine = filtered
                .GroupBy(r => ShortLineName(r.LineName))
                .Select(g => new { Line = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .Take(8)
                .ToList();

            if (byLine.Count == 0)
                byLine.Add(new { Line = "-", Count = 0 });

            AlertsByLineSeries = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Name = "Số cảnh báo",
                    Values = byLine.Select(x => x.Count).ToArray(),
                    Fill = new SolidColorPaint(new SKColor(59, 130, 246)),
                    Stroke = new SolidColorPaint(new SKColor(147, 197, 253)) { StrokeThickness = 1 },
                    DataLabelsPaint = new SolidColorPaint(axisLabelColor),
                    DataLabelsPosition = DataLabelsPosition.Top,
                    DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("0")
                }
            };

            LineAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Labels = byLine.Select(x => x.Line).ToArray(),
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 12,
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            LineYAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Name = "Số cảnh báo",
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 12,
                    MinLimit = 0,
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            var byHour = Enumerable.Range(0, 24)
                .Select(hour => filtered.Count(r => r.CreatedAt.Hour == hour))
                .ToArray();

            AlertsByHourSeries = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Name = "Cảnh báo",
                    Values = byHour,
                    Fill = new SolidColorPaint(new SKColor(34, 197, 94)),
                    Stroke = new SolidColorPaint(new SKColor(134, 239, 172)) { StrokeThickness = 1 }
                }
            };

            HourAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Labels = Enumerable.Range(0, 24).Select(h => $"{h}h").ToArray(),
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 11,
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            HourYAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Name = "Số cảnh báo",
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 11,
                    MinLimit = 0,
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            var windowStart = DateTimeOffset.Now.AddMinutes(-11);
            var realtimeLabels = Enumerable.Range(0, 12)
                .Select(i => windowStart.AddMinutes(i))
                .ToArray();
            var newAlertValues = realtimeLabels
                .Select(bucket => filtered.Count(r => r.CreatedAt >= bucket && r.CreatedAt < bucket.AddMinutes(1)))
                .ToArray();
            var highRiskValues = realtimeLabels
                .Select(bucket => filtered.Count(r => r.CreatedAt >= bucket && r.CreatedAt < bucket.AddMinutes(1) && r.Severity is "Cao" or "Nghiêm trọng"))
                .ToArray();
            var openValues = realtimeLabels
                .Select(bucket => filtered.Count(r => r.CreatedAt >= bucket && r.CreatedAt < bucket.AddMinutes(1) && r.IsOpen))
                .ToArray();

            RealtimeTrendSeries = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Name = "Cảnh báo mới/phút",
                    Values = newAlertValues,
                    Fill = new SolidColorPaint(new SKColor(59, 130, 246, 105)),
                    Stroke = new SolidColorPaint(new SKColor(147, 197, 253)) { StrokeThickness = 1 }
                },
                new LineSeries<int>
                {
                    Name = "Cao/nghiêm trọng",
                    Values = highRiskValues,
                    Fill = null,
                    Stroke = new SolidColorPaint(new SKColor(255, 82, 82)) { StrokeThickness = 3 },
                    GeometryFill = new SolidColorPaint(new SKColor(255, 82, 82)),
                    GeometryStroke = new SolidColorPaint(new SKColor(254, 202, 202)) { StrokeThickness = 2 },
                    GeometrySize = 7,
                    LineSmoothness = 0.45
                },
                new LineSeries<int>
                {
                    Name = "Chưa đóng",
                    Values = openValues,
                    Fill = null,
                    Stroke = new SolidColorPaint(new SKColor(251, 191, 36)) { StrokeThickness = 3 },
                    GeometryFill = new SolidColorPaint(new SKColor(251, 191, 36)),
                    GeometryStroke = new SolidColorPaint(new SKColor(254, 243, 199)) { StrokeThickness = 2 },
                    GeometrySize = 7,
                    LineSmoothness = 0.45
                }
            };

            RealtimeAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Labels = realtimeLabels.Select(x => x.ToString("HH:mm")).ToArray(),
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 11,
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            RealtimeYAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Name = "Cảnh báo/phút",
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 11,
                    MinLimit = 0,
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            var severityCounts = new[]
            {
                new { Name = "Nghiêm trọng", Count = filtered.Count(r => r.Severity == "Nghiêm trọng"), Color = new SKColor(255, 82, 82) },
                new { Name = "Cao", Count = filtered.Count(r => r.Severity == "Cao"), Color = new SKColor(255, 140, 66) },
                new { Name = "Trung bình", Count = filtered.Count(r => r.Severity == "Trung bình"), Color = new SKColor(255, 209, 102) },
                new { Name = "Thấp", Count = filtered.Count(r => r.Severity == "Thấp"), Color = new SKColor(63, 207, 142) }
            };

            SeverityDonutSeries = severityCounts
                .Where(item => item.Count > 0)
                .DefaultIfEmpty(new { Name = "Không có dữ liệu", Count = 1, Color = new SKColor(75, 85, 99) })
                .Select(item => new PieSeries<int>
                {
                    Name = item.Name,
                    Values = new[] { item.Count },
                    Fill = new SolidColorPaint(item.Color),
                    DataLabelsPaint = new SolidColorPaint(new SKColor(255, 255, 255)),
                    DataLabelsSize = 12,
                    DataLabelsPosition = PolarLabelsPosition.Middle,
                    DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString("0"),
                    HoverPushout = 8
                })
                .ToArray();

            var severeRate = Percent(severityCounts[0].Count + severityCounts[1].Count, filtered.Count);
            SeverityMixText.Text = $"Cao + nghiêm trọng chiếm {severeRate} trong dữ liệu đang lọc";
            SeverityCriticalLegendText.Text = $"Nghiêm trọng ({severityCounts[0].Count})";
            SeverityHighLegendText.Text = $"Cao ({severityCounts[1].Count})";
            SeverityMediumLegendText.Text = $"Trung bình ({severityCounts[2].Count})";
            SeverityLowLegendText.Text = $"Thấp ({severityCounts[3].Count})";

            var recentTotal = newAlertValues.Sum();
            var recentHighRisk = highRiskValues.Sum();
            var recentOpen = openValues.Sum();
            RealtimeWindowText.Text = $"12 phút gần nhất: {recentTotal} cảnh báo mới · {recentHighRisk} cao/nghiêm trọng · {recentOpen} chưa đóng";
        }

        private void UpdateDashboardOverviewCharts(
            IReadOnlyCollection<AlertHistoryRecord> filtered,
            SKColor axisLabelColor,
            SKColor axisGridColor)
        {
            const int criticalThreshold = 48;
            const int warningThreshold = 36;
            const int expectedValue = 18;

            var now = DateTimeOffset.Now;
            var selectedPeriod = SelectedText(PeriodFilterComboBox, WeekPeriod);
            var bucketCount = 12;
            var bucketSize = selectedPeriod == TodayPeriod ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            var startOfToday = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            var start = selectedPeriod == TodayPeriod
                ? new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(-(bucketCount - 1))
                : startOfToday.AddDays(-(bucketCount - 1));

            var buckets = Enumerable.Range(0, bucketCount)
                .Select(i => new
                {
                    Start = start.AddTicks(bucketSize.Ticks * i),
                    End = start.AddTicks(bucketSize.Ticks * (i + 1)),
                    Label = selectedPeriod == TodayPeriod
                        ? start.AddTicks(bucketSize.Ticks * i).ToString("HH'h'")
                        : start.AddTicks(bucketSize.Ticks * i).ToString("dd/MM")
                })
                .ToArray();

            var totalValues = buckets
                .Select(bucket => filtered.Count(r => r.CreatedAt >= bucket.Start && r.CreatedAt < bucket.End))
                .ToArray();
            var criticalValues = buckets
                .Select(bucket => filtered.Count(r => r.CreatedAt >= bucket.Start && r.CreatedAt < bucket.End && r.Severity == "Nghiêm trọng"))
                .ToArray();
            var warningValues = buckets
                .Select(bucket => filtered.Count(r => r.CreatedAt >= bucket.Start && r.CreatedAt < bucket.End && r.Severity is "Cao" or "Trung bình"))
                .ToArray();
            var thresholdCritical = Enumerable.Repeat(criticalThreshold, bucketCount).ToArray();
            var thresholdWarning = Enumerable.Repeat(warningThreshold, bucketCount).ToArray();
            var expected = Enumerable.Repeat(expectedValue, bucketCount).ToArray();
            var maxValue = new[] { totalValues.DefaultIfEmpty(0).Max(), criticalThreshold }
                .Max() + 8;

            OverviewTrendSeries = new ISeries[]
            {
                BuildThresholdLine("Ngưỡng nghiêm trọng", thresholdCritical, new SKColor(255, 82, 82, 170)),
                BuildThresholdLine("Ngưỡng cảnh báo", thresholdWarning, new SKColor(255, 209, 102, 170)),
                BuildThresholdLine("Giá trị kỳ vọng", expected, new SKColor(63, 207, 142, 170)),
                new LineSeries<int>
                {
                    Name = "Tổng số",
                    Values = totalValues,
                    Fill = new SolidColorPaint(new SKColor(37, 99, 235, 45)),
                    Stroke = new SolidColorPaint(new SKColor(59, 130, 246)) { StrokeThickness = 3 },
                    GeometryFill = new SolidColorPaint(new SKColor(59, 130, 246)),
                    GeometryStroke = new SolidColorPaint(new SKColor(191, 219, 254)) { StrokeThickness = 2 },
                    GeometrySize = 8,
                    LineSmoothness = 0.7
                },
                new LineSeries<int>
                {
                    Name = "Nghiêm trọng",
                    Values = criticalValues,
                    Fill = new SolidColorPaint(new SKColor(255, 82, 82, 35)),
                    Stroke = new SolidColorPaint(new SKColor(255, 82, 82)) { StrokeThickness = 3 },
                    GeometryFill = new SolidColorPaint(new SKColor(255, 82, 82)),
                    GeometrySize = 7,
                    LineSmoothness = 0.65
                },
                new LineSeries<int>
                {
                    Name = "Cảnh báo",
                    Values = warningValues,
                    Fill = null,
                    Stroke = new SolidColorPaint(new SKColor(255, 140, 66)) { StrokeThickness = 3 },
                    GeometryFill = new SolidColorPaint(new SKColor(255, 140, 66)),
                    GeometrySize = 7,
                    LineSmoothness = 0.65
                }
            };

            OverviewTrendAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Labels = buckets.Select(x => x.Label).ToArray(),
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 11,
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            OverviewTrendYAxes = new ICartesianAxis[]
            {
                new Axis
                {
                    Name = "Số cảnh báo",
                    LabelsPaint = new SolidColorPaint(axisLabelColor),
                    TextSize = 11,
                    MinLimit = 0,
                    MaxLimit = Math.Max(60, maxValue),
                    SeparatorsPaint = new SolidColorPaint(axisGridColor) { StrokeThickness = 1 }
                }
            };

            UpdateTopSources(filtered);
        }

        private static LineSeries<int> BuildThresholdLine(string name, int[] values, SKColor color)
        {
            return new LineSeries<int>
            {
                Name = name,
                Values = values,
                Fill = null,
                Stroke = new SolidColorPaint(color) { StrokeThickness = 2 },
                GeometrySize = 0,
                LineSmoothness = 0
            };
        }

        private void UpdateTopSources(IReadOnlyCollection<AlertHistoryRecord> filtered)
        {
            TopSources.Clear();

            var rows = filtered
                .GroupBy(SourceCodeForRecord)
                .Select(g => new
                {
                    SourceCode = g.Key,
                    AlertCount = g.Count(),
                    RiskScore = g.Sum(x => x.SeverityWeight) + g.Count(x => x.IsOpen)
                })
                .OrderByDescending(x => x.RiskScore)
                .ThenByDescending(x => x.AlertCount)
                .Take(7)
                .ToList();

            var maxCount = Math.Max(1, rows.Select(x => x.AlertCount).DefaultIfEmpty(1).Max());
            for (var i = 0; i < rows.Count; i++)
            {
                TopSources.Add(new TopSourceStat
                {
                    SourceCode = rows[i].SourceCode,
                    AlertCount = rows[i].AlertCount,
                    BarWidth = 260d * rows[i].AlertCount / maxCount,
                    BarBrush = TopSourceBrush(i)
                });
            }
        }

        private static string SourceCodeForRecord(AlertHistoryRecord record)
        {
            if (record.AlertType.Contains("camera", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(record.Camera) &&
                record.Camera != "-")
            {
                return record.Camera;
            }

            if (!string.IsNullOrWhiteSpace(record.Node))
            {
                return record.Node;
            }

            return string.IsNullOrWhiteSpace(record.Camera) || record.Camera == "-"
                ? "UNKNOWN"
                : record.Camera;
        }

        private static Brush TopSourceBrush(int index)
        {
            var color = index switch
            {
                0 => Color.FromArgb(255, 255, 82, 82),
                1 => Color.FromArgb(255, 255, 140, 66),
                2 => Color.FromArgb(255, 255, 209, 102),
                3 => Color.FromArgb(255, 255, 209, 102),
                _ => Color.FromArgb(255, 63, 207, 142)
            };

            return new SolidColorBrush(color);
        }

        private void UpdateHeatmap(IReadOnlyCollection<AlertHistoryRecord> filtered)
        {
            HeatmapCells.Clear();
            HeatmapHourBars.Clear();
            HeatmapDayBars.Clear();
            HeatmapHourLabels.Clear();

            foreach (var hour in Enumerable.Range(0, 24))
            {
                HeatmapHourLabels.Add(hour.ToString());
            }

            var now = DateTimeOffset.Now.Date;
            var dates = Enumerable.Range(0, 7)
                .Select(i => now.AddDays(-6 + i))
                .ToList();
            var maxCount = Math.Max(1, dates.SelectMany(date => Enumerable.Range(0, 24)
                .Select(hour => filtered.Count(r => r.CreatedAt.Date == date && r.CreatedAt.Hour == hour))).Max());
            var hourlyTotals = Enumerable.Range(0, 24)
                .Select(hour => filtered.Count(r => r.CreatedAt.Hour == hour))
                .ToArray();
            var dailyTotals = dates
                .Select(date => filtered.Count(r => r.CreatedAt.Date == date))
                .ToArray();
            var maxHourTotal = Math.Max(1, hourlyTotals.DefaultIfEmpty(0).Max());
            var maxDayTotal = Math.Max(1, dailyTotals.DefaultIfEmpty(0).Max());

            for (var hour = 0; hour < 24; hour++)
            {
                var total = hourlyTotals[hour];
                var ratio = total == 0 ? 0 : Math.Clamp(total / (double)maxHourTotal, 0.18, 1.0);
                HeatmapHourBars.Add(new HourBucketStat
                {
                    Hour = hour,
                    Count = total,
                    BarHeight = total == 0 ? 2 : 6 + ratio * 62,
                    Opacity = total == 0 ? 0.15 : 0.48 + ratio * 0.52,
                    Brush = HeatmapIntensityBrush(ratio, total == 0),
                    Tooltip = $"{hour:00}:00 - {total} cảnh báo"
                });
            }

            for (var dayIndex = 0; dayIndex < dates.Count; dayIndex++)
            {
                var date = dates[dayIndex];
                var dayTotal = dailyTotals[dayIndex];
                var dayRatio = dayTotal == 0 ? 0 : Math.Clamp(dayTotal / (double)maxDayTotal, 0.22, 1.0);
                HeatmapDayBars.Add(new DayBucketStat
                {
                    Label = date.ToString("dd/MM"),
                    Count = dayTotal,
                    BarWidth = dayTotal == 0 ? 18 : 28 + dayRatio * 72,
                    Opacity = dayTotal == 0 ? 0.22 : 0.55 + dayRatio * 0.45,
                    Brush = HeatmapIntensityBrush(dayRatio, dayTotal == 0),
                    Tooltip = $"{date:dd/MM/yyyy} - {dayTotal} cảnh báo"
                });

                for (var hour = 0; hour < 24; hour++)
                {
                    var count = filtered.Count(r => r.CreatedAt.Date == date && r.CreatedAt.Hour == hour);
                    var ratio = count == 0 ? 0 : Math.Clamp(count / (double)maxCount, 0.18, 1.0);
                    HeatmapCells.Add(new HeatmapCell
                    {
                        Count = count,
                        CountText = count == 0 ? string.Empty : count.ToString(),
                        CellOverlayBrush = HeatmapIntensityBrush(ratio, count == 0),
                        CellOpacity = count == 0
                            ? (IsDarkThemeActive() ? 0.40 : 0.72)
                            : 0.78 + ratio * 0.22,
                        GridLineBrush = GridLineBrush(),
                        CountForeground = count > 0
                            ? HeatmapCountForeground(ratio)
                            : ThemeBrush("TextPrimaryBrush"),
                        HighlightBrush = count > 0 && ratio >= 0.82
                            ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
                            : new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)),
                        HighlightThickness = count > 0 && ratio >= 0.82 ? 1 : 0,
                        Tooltip = $"{date:dd/MM/yyyy} {hour:00}:00 - {count} cảnh báo"
                    });
                }
            }

            HeatmapTotalText.Text = $"{filtered.Count} cảnh báo";

            var peak = filtered
                .GroupBy(r => r.CreatedAt.Hour)
                .Select(g => new { Hour = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .FirstOrDefault();

            PeakHourText.Text = peak == null
                ? "Chưa có khung giờ nổi bật"
                : $"Cao điểm: {peak.Hour:00}:00 · {peak.Count} cảnh báo";
        }

        private void UpdateTopNodes(IReadOnlyCollection<AlertHistoryRecord> filtered)
        {
            TopNodes.Clear();

            var rows = filtered
                .GroupBy(r => new { r.Node, r.LineName })
                .Select(g => new TopNodeStat
                {
                    NodeCode = g.Key.Node,
                    LineName = g.Key.LineName,
                    AlertCount = g.Count(),
                    RiskScore = g.Sum(x => x.SeverityWeight) + g.Count(x => x.IsOpen)
                })
                .OrderByDescending(x => x.RiskScore)
                .ThenByDescending(x => x.AlertCount)
                .Take(6)
                .ToList();

            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].Rank = i + 1;
                TopNodes.Add(rows[i]);
            }
        }

        private void UpdateHistory(IEnumerable<AlertHistoryRecord> filtered)
        {
            History.Clear();
            foreach (var record in filtered.Take(24))
                History.Add(record);
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingFilters)
                return;

            _updatingFilters = true;
            if (ReferenceEquals(sender, LineFilterComboBox))
            {
                RefreshNodeOptions();
                RefreshCameraOptions();
            }
            else if (ReferenceEquals(sender, NodeFilterComboBox))
            {
                RefreshCameraOptions();
            }
            _updatingFilters = false;

            ApplyAnalytics();
        }

        private static string SelectedText(ComboBox comboBox, string fallback)
        {
            return comboBox.SelectedItem as string ?? fallback;
        }

        private static string FormatChange(int current, int previous)
        {
            if (previous == 0)
                return current == 0 ? "+0%" : "+100%";

            var change = (current - previous) * 100.0 / previous;
            return $"{change:+0;-0;0}%";
        }

        private static string Percent(int value, int total)
        {
            if (total == 0)
                return "0%";
            return $"{Math.Round(value * 100.0 / total):0}%";
        }

        private static SolidColorBrush BrushForChange(int delta)
        {
            return new SolidColorBrush(delta > 0
                ? Color.FromArgb(255, 255, 82, 82)
                : Color.FromArgb(255, 63, 207, 142));
        }

        private SolidColorBrush HeatmapIntensityBrush(double ratio, bool isEmpty)
        {
            if (isEmpty)
            {
                return IsDarkThemeActive()
                    ? new SolidColorBrush(Color.FromArgb(255, 20, 28, 43))
                    : new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
            }

            ratio = Math.Clamp(ratio, 0.18, 1.0);
            var color = ratio switch
            {
                < 0.34 => InterpolateColor(
                    Color.FromArgb(255, 96, 165, 250),
                    Color.FromArgb(255, 129, 140, 248),
                    ratio / 0.34),
                < 0.66 => InterpolateColor(
                    Color.FromArgb(255, 129, 140, 248),
                    Color.FromArgb(255, 192, 132, 252),
                    (ratio - 0.34) / 0.32),
                _ => InterpolateColor(
                    Color.FromArgb(255, 192, 132, 252),
                    Color.FromArgb(255, 251, 113, 133),
                    (ratio - 0.66) / 0.34)
            };

            return new SolidColorBrush(color);
        }

        private static Color InterpolateColor(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return Color.FromArgb(
                255,
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private SolidColorBrush GridLineBrush()
        {
            return IsDarkThemeActive()
                ? new SolidColorBrush(Color.FromArgb(68, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(82, 255, 255, 255));
        }

        private static SolidColorBrush HeatmapCountForeground(double ratio)
        {
            return ratio < 0.42
                ? new SolidColorBrush(Color.FromArgb(255, 15, 23, 42))
                : new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        }

        private static string EstimateAverageHandleTime(IReadOnlyCollection<AlertHistoryRecord> filtered)
        {
            if (filtered.Count == 0)
                return "-";

            var minutes = Math.Clamp(filtered.Average(r => r.SeverityWeight * 18 + (r.IsOpen ? 12 : 0)), 15, 180);
            return minutes >= 60
                ? $"{minutes / 60:0.0} giờ"
                : $"{minutes:0} phút";
        }

        private static int SeverityWeight(AlertSeverity severity)
        {
            return severity switch
            {
                AlertSeverity.Critical => 4,
                AlertSeverity.High => 3,
                AlertSeverity.Medium => 2,
                _ => 1
            };
        }

        private static string SeverityToText(AlertSeverity severity)
        {
            return severity switch
            {
                AlertSeverity.Critical => "Nghiêm trọng",
                AlertSeverity.High => "Cao",
                AlertSeverity.Medium => "Trung bình",
                _ => "Thấp"
            };
        }

        private static string StateToText(AlertState state)
        {
            return state switch
            {
                AlertState.Unprocessed => "Chờ xử lý",
                AlertState.Acknowledged => "Đã xác nhận",
                AlertState.InProgress => "Đang xử lý",
                AlertState.Resolved => "Đã đóng",
                AlertState.Closed => "Đã đóng",
                _ => "Chờ xử lý"
            };
        }

        private static string CategoryToText(AlertCategory category)
        {
            return category switch
            {
                AlertCategory.Temperature => "Nhiệt độ vượt ngưỡng",
                AlertCategory.Humidity => "Độ ẩm vượt ngưỡng",
                AlertCategory.Radar => "Radar phát hiện người",
                AlertCategory.Infrared => "Hồng ngoại kích hoạt",
                AlertCategory.Light => "Ánh sáng bất thường",
                AlertCategory.Accelerometer => "Rung động bất thường",
                AlertCategory.Intrusion => "Phát hiện xâm nhập",
                AlertCategory.Equipment => "Thiết bị bất thường",
                AlertCategory.Connection => "Mất kết nối",
                _ => "Cảnh báo khác"
            };
        }

        private static string ShortLineName(string lineName)
        {
            if (string.IsNullOrWhiteSpace(lineName))
                return "-";

            return lineName
                .Replace("Tuyến cống ", string.Empty)
                .Replace("Cống ", string.Empty)
                .Replace("Tuyến ", string.Empty)
                .Trim();
        }

        private async void ExportExcel_Click(object sender, RoutedEventArgs e)
        {
            var path = ReportExporter.ExportHistoryToExcel(
                History,
                TopNodes,
                "TRM-HN-001",
                "Trạm Nghĩa Đô");

            await ShowExportDoneDialogAsync("Excel", path);
        }

        private async void ExportPdf_Click(object sender, RoutedEventArgs e)
        {
            await ShowExportDoneDialogAsync("PDF", "Chức năng xuất PDF đang được hoàn thiện.");
        }

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _themeService.ToggleTheme();
        }

        private void UpdateThemeIcons()
        {
            var isDark = IsDarkThemeActive();
            MoonIcon.Visibility = isDark ? Visibility.Visible : Visibility.Collapsed;
            SunIcon.Visibility = isDark ? Visibility.Collapsed : Visibility.Visible;
        }

        private bool IsDarkThemeActive()
        {
            return _themeService.CurrentTheme != ElementTheme.Light;
        }

        private static SolidColorBrush ThemeBrush(string key)
        {
            return Station.Services.ThemeBrushes.TryGet(key, out var value) && value is SolidColorBrush brush
                ? brush
                : new SolidColorBrush(Color.FromArgb(255, 15, 23, 42));
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private async Task ShowExportDoneDialogAsync(string kind, string path)
        {
            var dialog = new ContentDialog
            {
                Title = $"Xuất file {kind}",
                Content = $"Đã xử lý yêu cầu xuất {kind}.\n{path}",
                CloseButtonText = "Đóng",
                XamlRoot = Content.XamlRoot
            };

            await dialog.ShowAsync();
        }
    }

    public class TopNodeStat
    {
        public int Rank { get; set; }
        public string NodeCode { get; set; } = "";
        public string LineName { get; set; } = "";
        public int AlertCount { get; set; }
        public int RiskScore { get; set; }
    }

    public class TopSourceStat
    {
        public string SourceCode { get; set; } = "";
        public int AlertCount { get; set; }
        public double BarWidth { get; set; }
        public Brush BarBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 52, 211, 153));
    }

    public class HourBucketStat
    {
        public int Hour { get; set; }
        public int Count { get; set; }
        public double BarHeight { get; set; }
        public double Opacity { get; set; } = 0.2;
        public Brush Brush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250));
        public string Tooltip { get; set; } = "";
    }

    public class DayBucketStat
    {
        public string Label { get; set; } = "";
        public int Count { get; set; }
        public double BarWidth { get; set; }
        public double Opacity { get; set; } = 0.2;
        public Brush Brush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250));
        public string Tooltip { get; set; } = "";
    }

    public class AlertHistoryRecord
    {
        public DateTimeOffset CreatedAt { get; set; }
        public string Timestamp { get; set; } = "";
        public string Line { get; set; } = "";
        public string LineName { get; set; } = "";
        public string Node { get; set; } = "";
        public string Camera { get; set; } = "";
        public string AlertType { get; set; } = "";
        public string Severity { get; set; } = "";
        public int SeverityWeight { get; set; }
        public string Status { get; set; } = "";
        public bool IsOpen { get; set; }
        public bool IsRealtime { get; set; }
    }

    public class HeatmapCell
    {
        public int Count { get; set; }
        public string CountText { get; set; } = "";
        public double CellOpacity { get; set; } = 0.36;
        public Brush CellOverlayBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        public Brush GridLineBrush { get; set; } = new SolidColorBrush(Color.FromArgb(68, 255, 255, 255));
        public Brush CountForeground { get; set; } = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        public Brush HighlightBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
        public double HighlightThickness { get; set; }
        public string Tooltip { get; set; } = "";
    }

    internal readonly record struct LocationInfo(string LineName, string NodeName, string CameraId);
}
