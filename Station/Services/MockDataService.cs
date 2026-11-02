using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Station.Models;

namespace Station.Services
{
    // ═══════════════════════════════════════════════════════════════════
    //  Event args
    // ═══════════════════════════════════════════════════════════════════

    public class SensorTickEventArgs : EventArgs
    {
        public SimulatedSensor Sensor { get; init; } = null!;
        public double NewValue { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public bool IsAnomaly { get; init; }
    }

    public class AlertGeneratedEventArgs : EventArgs
    {
        public Alert Alert { get; init; } = null!;
        public string? TriggeredByCameraId { get; init; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SimulatedSensor
    // ═══════════════════════════════════════════════════════════════════

    public class SimulatedSensor
    {
        public string SensorId { get; set; } = string.Empty;
        public string SensorName { get; set; } = string.Empty;
        public AlertCategory Category { get; set; }
        public string Location { get; set; } = string.Empty;       // e.g. "Hầm A1 - Cửa van số 3"
        public string NodeId { get; set; } = string.Empty;
        public string NodeName { get; set; } = string.Empty;
        public string LineId { get; set; } = string.Empty;
        public string LineName { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;

        // Simulation parameters
        public double CurrentValue { get; set; }
        public double NominalValue { get; set; }    // Centre of normal range
        public double DriftSpeed { get; set; }      // Max change per tick
        public double MinNormal { get; set; }
        public double MaxNormal { get; set; }
        public double WarnThreshold { get; set; }
        public double CriticalThreshold { get; set; }
        public double AbsoluteMin { get; set; }
        public double AbsoluteMax { get; set; }

        // Fault injection state (set externally to test)
        public bool IsInFaultMode { get; set; } = false;
        public bool IsOnline { get; set; } = true;

        // Derived
        public SensorAlertLevel CurrentLevel =>
            !IsOnline ? SensorAlertLevel.Offline :
            CurrentValue >= CriticalThreshold ? SensorAlertLevel.Critical :
            CurrentValue >= WarnThreshold ? SensorAlertLevel.Warning :
            SensorAlertLevel.Normal;

        public AlertSeverity CurrentAlertSeverity =>
            CurrentValue >= CriticalThreshold ? AlertSeverity.Critical :
            CurrentValue >= WarnThreshold ? AlertSeverity.High :
            AlertSeverity.Low;

        public string StatusText => CurrentLevel switch
        {
            SensorAlertLevel.Critical => "Khẩn cấp",
            SensorAlertLevel.Warning => "Cảnh báo",
            SensorAlertLevel.Offline => "Mất kết nối",
            _ => "Bình thường"
        };
    }

    public enum SensorAlertLevel { Normal, Warning, Critical, Offline }

    // ═══════════════════════════════════════════════════════════════════
    //  Camera simulation entry
    // ═══════════════════════════════════════════════════════════════════

    public class SimulatedCamera
    {
        public string CameraId { get; set; } = string.Empty;
        public string CameraName { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string NodeName { get; set; } = string.Empty;
        public string LineId { get; set; } = string.Empty;
        public string LineName { get; set; } = string.Empty;
        public bool IsOnline { get; set; } = true;
        /// <summary>RTSP/HLS stream URL — null khi dùng mock</summary>
        public string? StreamUrl { get; set; }

        // BackendV2 enrichment (khớp BackendV2.Models.Camera) — mock để mặc định,
        // RealDataService/BackendV2 data service map từ CameraDto vào các field này.
        public string? Description { get; set; }
        public int Protocol { get; set; }   // 0=RTSP, 1=HTTP, 2=WebSocket
        public int Status { get; set; }     // 0=Online, 1=Offline, 2=Error
        public string? Resolution { get; set; }
        public int? Fps { get; set; }
        public string? Codec { get; set; }
        public bool IrEnabled { get; set; }
        public bool HdrEnabled { get; set; }
        public bool IsRecording { get; set; }
        public DateTime? LastFrameTime { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MockDataService  (Singleton)
    // ═══════════════════════════════════════════════════════════════════

    public sealed class MockDataService : IDataService
    {
        // ── Singleton ──────────────────────────────────────────────────
        private static readonly Lazy<MockDataService> _instance =
            new(() => new MockDataService());
        public static MockDataService Instance => _instance.Value;

        // ── Events ─────────────────────────────────────────────────────
        /// Fired every tick for every sensor with its new reading
        public event EventHandler<SensorTickEventArgs>? SensorTick;
        public event EventHandler<AlertGeneratedEventArgs>? AlertGenerated;
        public event EventHandler? TopologyLoaded;
        public event EventHandler<JoinRequestNotification>? NewJoinRequest;

        // ── Public state ───────────────────────────────────────────────
        public IReadOnlyList<SimulatedSensor> Sensors { get; }
        public IReadOnlyList<SimulatedCamera> Cameras { get; }
        public IReadOnlyList<TunnelLine> Lines { get; }
        public IReadOnlyList<TunnelNode> Nodes { get; }
        public ObservableCollection<Alert> ActiveAlerts { get; } = new();
        public ObservableCollection<Alert> AlertHistory { get; } = new();

        // ── Private ────────────────────────────────────────────────────
        private readonly Random _rng = new();
        private readonly Timer _sensorTimer;
        private readonly Timer _alertTimer;

        // Per-sensor cooldown so we don't spam alerts
        private readonly Dictionary<string, DateTimeOffset> _alertCooldowns = new();
        private static readonly TimeSpan AlertCooldown = TimeSpan.FromSeconds(30);

        // Camera intrusion cooldown
        private readonly Dictionary<string, DateTimeOffset> _cameraAlertCooldowns = new();
        private static readonly TimeSpan CameraAlertCooldown = TimeSpan.FromSeconds(45);

        // ── Constructor ────────────────────────────────────────────────
        private MockDataService()
        {
            Sensors = BuildSensors();
            Cameras = BuildCameras();
            Lines = BuildLines();
            Nodes = Lines.SelectMany(l => l.Nodes).ToList();

            // Sensor tick: every 1s — updates readings & fires SensorTick
            _sensorTimer = new Timer(1000);
            _sensorTimer.Elapsed += OnSensorTick;
            _sensorTimer.AutoReset = true;

            // Alert evaluation: every 4s — checks thresholds & fires random camera alerts
            _alertTimer = new Timer(4000);
            _alertTimer.Elapsed += OnAlertEvaluation;
            _alertTimer.AutoReset = true;
        }

        // ── Start / Stop ───────────────────────────────────────────────
        public void Start()
        {
            _sensorTimer.Start();
            _alertTimer.Start();
        }

        public void Stop()
        {
            _sensorTimer.Stop();
            _alertTimer.Stop();
        }

        // ── Join-request stubs (mock always returns empty / false) ─────
        public Task<IReadOnlyList<JoinRequestNotification>> GetPendingJoinRequestsAsync()
            => Task.FromResult<IReadOnlyList<JoinRequestNotification>>(Array.Empty<JoinRequestNotification>());

        public Task<bool> ApproveJoinRequestAsync(int requestId, byte nodeByteId)
            => Task.FromResult(false);

        public Task<bool> RejectJoinRequestAsync(int requestId, string? reason = null)
            => Task.FromResult(false);

        // ══════════════════════════════════════════════════════════════
        //  Sensor tick — random walk all sensor values
        // ══════════════════════════════════════════════════════════════

        private void OnSensorTick(object? sender, ElapsedEventArgs e)
        {
            foreach (var sensor in Sensors)
            {
                if (!sensor.IsOnline) continue;

                double newValue = sensor.IsInFaultMode
                    ? SimulateFaultSpike(sensor)
                    : RandomWalk(sensor);

                sensor.CurrentValue = newValue;

                SensorTick?.Invoke(this, new SensorTickEventArgs
                {
                    Sensor = sensor,
                    NewValue = newValue,
                    Timestamp = DateTimeOffset.Now,
                    IsAnomaly = sensor.CurrentLevel >= SensorAlertLevel.Warning
                });
            }
        }

        /// Gaussian-biased random walk — values drift toward nominal, occasionally spike
        private double RandomWalk(SimulatedSensor s)
        {
            // Revert-to-mean force (5% per tick)
            double reversion = (s.NominalValue - s.CurrentValue) * 0.05;

            // Random noise proportional to drift speed
            double noise = (_rng.NextDouble() * 2 - 1) * s.DriftSpeed;

            // Rare spike (1.5% chance — push toward or past threshold)
            if (_rng.NextDouble() < 0.015)
                noise += (_rng.NextDouble() > 0.5 ? 1 : -1) * s.DriftSpeed * 6;

            double next = s.CurrentValue + reversion + noise;
            return Math.Clamp(next, s.AbsoluteMin, s.AbsoluteMax);
        }

        private double SimulateFaultSpike(SimulatedSensor s)
        {
            // In fault mode, value ramps toward critical
            double target = s.AbsoluteMax;
            double step = s.DriftSpeed * 3;
            return Math.Min(s.CurrentValue + step, target);
        }

        // ══════════════════════════════════════════════════════════════
        //  Alert evaluation — threshold crossing + camera events
        // ══════════════════════════════════════════════════════════════

        private void OnAlertEvaluation(object? sender, ElapsedEventArgs e)
        {
            // Only evaluate real threshold-based alerts
            // Auto-generated random alerts have been disabled
            EvaluateSensorAlerts();
            // MaybeFireCameraAlert();       // DISABLED - was generating fake camera alerts
            // MaybeFireRandomSensorAlert(); // DISABLED - was generating fake sensor alerts
        }

        private void EvaluateSensorAlerts()
        {
            foreach (var sensor in Sensors)
            {
                if (!sensor.IsOnline) continue;
                if (sensor.CurrentLevel == SensorAlertLevel.Normal) continue;

                // Check cooldown
                string key = $"sensor:{sensor.SensorId}";
                if (_alertCooldowns.TryGetValue(key, out var last) &&
                    DateTimeOffset.Now - last < AlertCooldown) continue;

                var alert = BuildSensorAlert(sensor);
                RegisterAlert(alert);
                _alertCooldowns[key] = DateTimeOffset.Now;
                AlertGenerated?.Invoke(this, new AlertGeneratedEventArgs { Alert = alert });
            }
        }

        private void MaybeFireCameraAlert()
        {
            // ~8% chance per 4-second evaluation to generate a camera alert
            if (_rng.NextDouble() > 0.08) return;

            var onlineCams = Cameras.Where(c => c.IsOnline).ToList();
            if (onlineCams.Count == 0) return;

            var cam = onlineCams[_rng.Next(onlineCams.Count)];
            string key = $"cam:{cam.CameraId}";
            if (_alertCooldowns.TryGetValue(key, out var last) &&
                DateTimeOffset.Now - last < CameraAlertCooldown) return;

            var alert = BuildCameraAlert(cam);
            RegisterAlert(alert);
            _alertCooldowns[key] = DateTimeOffset.Now;
            AlertGenerated?.Invoke(this, new AlertGeneratedEventArgs
            {
                Alert = alert,
                TriggeredByCameraId = cam.CameraId
            });
        }

        private void MaybeFireRandomSensorAlert()
        {
            // ~3% chance — random sensor anomaly (brief spike already logged by walk, this fires an alert)
            if (_rng.NextDouble() > 0.03) return;

            // Pick a sensor that is currently just below warning — give it a push
            var candidate = Sensors
                .Where(s => s.IsOnline && !s.IsInFaultMode &&
                            s.CurrentValue >= s.MaxNormal * 0.85 &&
                            s.CurrentValue < s.WarnThreshold)
                .OrderByDescending(s => s.CurrentValue / s.WarnThreshold)
                .FirstOrDefault();

            if (candidate == null) return;

            // Force it over the warning threshold
            candidate.CurrentValue = candidate.WarnThreshold * (1.0 + _rng.NextDouble() * 0.15);
        }

        // ══════════════════════════════════════════════════════════════
        //  Alert builders
        // ══════════════════════════════════════════════════════════════

        private Alert BuildSensorAlert(SimulatedSensor sensor)
        {
            var (title, description) = GetSensorAlertText(sensor);
            return new Alert
            {
                Title = title,
                Description = description,
                Category = sensor.Category,
                Severity = sensor.CurrentAlertSeverity,
                State = AlertState.Unprocessed,
                LineId   = sensor.LineId,
                LineName = sensor.LineName,
                NodeId = sensor.NodeId,
                NodeName = sensor.NodeName,
                SensorId = sensor.SensorId,
                SensorName = sensor.SensorName,
                SensorType = sensor.Category.ToString(),
                SensorValue = Math.Round(sensor.CurrentValue, 2),
                SensorUnit = sensor.Unit,
                Threshold = sensor.CurrentAlertSeverity == AlertSeverity.Critical
                    ? sensor.CriticalThreshold : sensor.WarnThreshold,
                CreatedAt = DateTimeOffset.Now
            };
        }

        private Alert BuildCameraAlert(SimulatedCamera cam)
        {
            var scenarios = _cameraAlertScenarios;
            var scenario = scenarios[_rng.Next(scenarios.Length)];
            return new Alert
            {
                Title = scenario.Title,
                Description = scenario.Description,
                Category = AlertCategory.Intrusion,
                Severity = scenario.Severity,
                State = AlertState.Unprocessed,
                LineId   = cam.LineId,
                LineName = cam.LineName,
                NodeId = cam.NodeId,
                NodeName = cam.NodeName,
                CameraId = cam.CameraId,
                CreatedAt = DateTimeOffset.Now
            };
        }

        private void RegisterAlert(Alert alert)
        {
            ActiveAlerts.Insert(0, alert);
            AlertHistory.Insert(0, alert);

            // Keep history capped at 200
            while (AlertHistory.Count > 200)
                AlertHistory.RemoveAt(AlertHistory.Count - 1);
        }

        // ══════════════════════════════════════════════════════════════
        //  Public helpers
        // ══════════════════════════════════════════════════════════════

        public void AcknowledgeAlert(Alert alert, string by = "Operator")
        {
            alert.State = AlertState.Acknowledged;
            alert.AcknowledgedAt = DateTimeOffset.Now;
            alert.AcknowledgedBy = by;
            ActiveAlerts.Remove(alert);
        }

        public void ResolveAlert(Alert alert, string by = "Operator")
        {
            alert.State = AlertState.Resolved;
            alert.ResolvedAt = DateTimeOffset.Now;
            alert.ResolvedBy = by;
            ActiveAlerts.Remove(alert);
        }

        /// Inject a fault into a sensor to force a critical alert (for demo/testing)
        public void InjectSensorFault(string sensorId)
        {
            var sensor = Sensors.FirstOrDefault(s => s.SensorId == sensorId);
            if (sensor != null) sensor.IsInFaultMode = true;
        }

        public void ClearSensorFault(string sensorId)
        {
            var sensor = Sensors.FirstOrDefault(s => s.SensorId == sensorId);
            if (sensor != null) sensor.IsInFaultMode = false;
        }

        /// Fire a manually-crafted alert (from web dashboard or tests)
        public void FireManualAlert(
            string title,
            string description,
            AlertSeverity severity,
            AlertCategory category,
            string nodeId = "NODE-A1",
            string nodeName = "Nút A1")
        {
            var alert = new Alert
            {
                Title = title ?? string.Empty,
                Description = description ?? string.Empty,
                Category = category,
                Severity = severity,
                State = AlertState.Unprocessed,
                LineId = "LINE-01",
                LineName = "Tuyến hầm chính",
                NodeId = nodeId,
                NodeName = nodeName,
                CreatedAt = DateTimeOffset.Now
            };
            RegisterAlert(alert);
            AlertGenerated?.Invoke(this, new AlertGeneratedEventArgs { Alert = alert });
        }

        /// Update simulation parameters for a sensor at runtime
        public void UpdateSensorParams(string sensorId, double? nominalValue, double? driftSpeed,
            double? warnThreshold, double? criticalThreshold)
        {
            var sensor = Sensors.FirstOrDefault(s => s.SensorId == sensorId);
            if (sensor == null) return;
            if (nominalValue.HasValue)      sensor.NominalValue      = nominalValue.Value;
            if (driftSpeed.HasValue)        sensor.DriftSpeed        = driftSpeed.Value;
            if (warnThreshold.HasValue)     sensor.WarnThreshold     = warnThreshold.Value;
            if (criticalThreshold.HasValue) sensor.CriticalThreshold = criticalThreshold.Value;
        }

        // ══════════════════════════════════════════════════════════════
        //  Line / Node hierarchy (derived from sensor data)
        // ══════════════════════════════════════════════════════════════

        private IReadOnlyList<TunnelLine> BuildLines()
        {
            var lineIds = Sensors.Select(s => s.LineId).Distinct().ToList();
            return lineIds.Select(lid =>
            {
                var firstSensor = Sensors.First(s => s.LineId == lid);
                var nodeIds = Sensors
                    .Where(s => s.LineId == lid)
                    .Select(s => s.NodeId)
                    .Distinct()
                    .ToList();
                return new TunnelLine
                {
                    LineId = lid,
                    LineName = firstSensor.LineName,
                    Nodes = nodeIds.Select(nid =>
                    {
                        var ns = Sensors.First(s => s.NodeId == nid);
                        return new TunnelNode
                        {
                            NodeId = nid,
                            NodeName = ns.NodeName,
                            LineId = lid,
                            LineName = firstSensor.LineName
                        };
                    }).ToList()
                };
            }).ToList();
        }

		// ══════════════════════════════════════════════════════════════
		//  Sensor definitions (3 lines x 4 nodes x 6 sensors)
		// ══════════════════════════════════════════════════════════════

		private List<SimulatedSensor> BuildSensors()
		{
			var sensors = new List<SimulatedSensor>();
			var lines = new[]
			{
				("LINE-DC", "Tuyến Đội Cấn", "dc"),
				("LINE-KM", "Tuyến Kim Mã", "km"),
				("LINE-HHT", "Tuyến Hoàng Hoa Thám", "hht"),
				("LINE-PDP", "Tuyến Phan Đình Phùng", "pdp")
			};

			foreach (var (lineId, lineName, prefix) in lines)
			{
				// Mỗi tuyến có 10 hố ga (01 đến 10)
				for (int ni = 1; ni <= 10; ni++)
				{
					string nodeId = $"{prefix}-{ni:D2}"; // Khớp 100% ID trong JSON (VD: dc-01)
					string nodeName = $"Hố ga {lineName.Replace("Tuyến ", "")} {ni}";
					string loc = nodeName; // Vị trí = tên node, giống dashboard giám sát

					// Radar
					sensors.Add(new SimulatedSensor
					{
						SensorId = $"RAD-{prefix.ToUpper()}-{ni:D2}",
						SensorName = $"Radar {nodeName}",
						Category = AlertCategory.Radar,
						LineId = lineId,
						LineName = lineName,
						NodeId = nodeId,
						NodeName = nodeName,
						Location = loc,
						Unit = "%",
						NominalValue = 5,
						CurrentValue = 5,
						MinNormal = 0,
						MaxNormal = 50,
						WarnThreshold = 60,
						CriticalThreshold = 85,
						AbsoluteMin = 0,
						AbsoluteMax = 100,
						DriftSpeed = 2.0
					});

					// Hồng ngoại (PIR)
					sensors.Add(new SimulatedSensor
					{
						SensorId = $"PIR-{prefix.ToUpper()}-{ni:D2}",
						SensorName = $"Hồng ngoại {nodeName}",
						Category = AlertCategory.Infrared,
						LineId = lineId,
						LineName = lineName,
						NodeId = nodeId,
						NodeName = nodeName,
						Location = loc,
						Unit = "%",
						NominalValue = 5,
						CurrentValue = 5,
						MinNormal = 0,
						MaxNormal = 50,
						WarnThreshold = 60,
						CriticalThreshold = 85,
						AbsoluteMin = 0,
						AbsoluteMax = 100,
						DriftSpeed = 3.0
					});

					// Nhiệt độ
					sensors.Add(new SimulatedSensor
					{
						SensorId = $"TMP-{prefix.ToUpper()}-{ni:D2}",
						SensorName = $"Nhiệt độ {nodeName}",
						Category = AlertCategory.Temperature,
						LineId = lineId,
						LineName = lineName,
						NodeId = nodeId,
						NodeName = nodeName,
						Location = loc,
						Unit = "°C",
						NominalValue = 25,
						CurrentValue = 25,
						MinNormal = 18,
						MaxNormal = 32,
						WarnThreshold = 45,
						CriticalThreshold = 60,
						AbsoluteMin = -5,
						AbsoluteMax = 80,
						DriftSpeed = 0.4
					});

					// Độ ẩm
					sensors.Add(new SimulatedSensor
					{
						SensorId = $"HUM-{prefix.ToUpper()}-{ni:D2}",
						SensorName = $"Độ ẩm {nodeName}",
						Category = AlertCategory.Humidity,
						LineId = lineId,
						LineName = lineName,
						NodeId = nodeId,
						NodeName = nodeName,
						Location = loc,
						Unit = "%RH",
						NominalValue = 55,
						CurrentValue = 55,
						MinNormal = 40,
						MaxNormal = 70,
						WarnThreshold = 85,
						CriticalThreshold = 95,
						AbsoluteMin = 10,
						AbsoluteMax = 99,
						DriftSpeed = 0.8
					});
				}
			}
			return sensors;
		}
		// ══════════════════════════════════════════════════════════════
		//  Camera definitions (16 cameras matching LiveVideoViewModel)
		// ══════════════════════════════════════════════════════════════

		private List<SimulatedCamera> BuildCameras()
		{
			var cams = new List<SimulatedCamera>();
			var lines = new[]
			{
				("LINE-DC", "Tuyến Đội Cấn", "dc"),
				("LINE-KM", "Tuyến Kim Mã", "km"),
				("LINE-HHT", "Tuyến Hoàng Hoa Thám", "hht"),
				("LINE-PDP", "Tuyến Phan Đình Phùng", "pdp")
			};

			foreach (var (lineId, lineName, prefix) in lines)
			{
				for (int ni = 1; ni <= 10; ni++)
				{
					string camNodeName = $"Hố ga {lineName.Replace("Tuyến ", "")} {ni}";
					cams.Add(new SimulatedCamera
					{
						CameraId = $"CAM-{prefix.ToUpper()}{ni:D2}",
						CameraName = $"Camera {camNodeName}",
						Location = camNodeName,
						LineId = lineId,
						LineName = lineName,
						NodeId = $"{prefix}-{ni:D2}", // Khớp với dc-01
						NodeName = camNodeName,
						IsOnline = true
					});
				}
			}
			return cams;
		}
		// ══════════════════════════════════════════════════════════════
		//  Alert text templates
		// ══════════════════════════════════════════════════════════════

		private static (string Title, string Description) GetSensorAlertText(SimulatedSensor s)
        {
            string level = s.CurrentAlertSeverity == AlertSeverity.Critical ? "nguy hiểm" : "cao";
            return s.Category switch
            {
                AlertCategory.Temperature => (
                    $"Nhiệt độ {level} tại {s.NodeName}",
                    $"Cảm biến {s.SensorName} ghi nhận {s.CurrentValue:F1}{s.Unit} — vượt ngưỡng {(s.CurrentAlertSeverity == AlertSeverity.Critical ? s.CriticalThreshold : s.WarnThreshold)}{s.Unit}. Kiểm tra thông gió ngay."),
                AlertCategory.Humidity => (
                    $"Độ ẩm {level} tại {s.NodeName}",
                    $"Cảm biến {s.SensorName} ghi nhận {s.CurrentValue:F1}{s.Unit}. Nguy cơ ăn mòn thiết bị điện. Kích hoạt hệ thống hút ẩm."),
                AlertCategory.Radar => (
                    $"Radar phát hiện người tại {s.NodeName}",
                    $"Radar {s.SensorName} ghi nhận xác suất hiện diện {s.CurrentValue:F0}{s.Unit}. {(s.CurrentAlertSeverity == AlertSeverity.Critical ? "KHẨN CẤP: Xác nhận xâm nhập trái phép, điều phối bảo vệ ngay!" : "Cần xác minh hiện diện bất thường trong khu vực.")}"),
                AlertCategory.Infrared => (
                    $"Cảm biến hồng ngoại kích hoạt tại {s.NodeName}",
                    $"Cảm biến PIR {s.SensorName} phát hiện chuyển động nhiệt. Kết hợp với camera để xác nhận."),
                AlertCategory.Light => (
                    $"Ánh sáng bất thường tại {s.NodeName}",
                    $"Cảm biến ánh sáng ghi nhận {s.CurrentValue:F0}{s.Unit}. Kiểm tra nguồn sáng lạ hoặc hệ thống chiếu sáng."),
                AlertCategory.Accelerometer => (
                    $"Rung động {level} tại {s.NodeName}",
                    $"Gia tốc kế {s.SensorName} ghi nhận {s.CurrentValue:F2}{s.Unit}. {(s.CurrentAlertSeverity == AlertSeverity.Critical ? "NGUY HIỂM: Kiểm tra kết cấu cống ngay lập tức!" : "Theo dõi kết cấu cống và thiết bị cơ khí.")}"),
                _ => (
                    $"Cảnh báo cảm biến tại {s.NodeName}",
                    $"Cảm biến {s.SensorName} ghi nhận giá trị bất thường: {s.CurrentValue:F2}{s.Unit}.")
            };
        }

        private readonly (string Title, string Description, AlertSeverity Severity)[] _cameraAlertScenarios =
        {
            (
                "Phát hiện xâm nhập trái phép",
                "Hệ thống AI phát hiện người xâm nhập vào khu vực hầm cấm. Đối tượng đang di chuyển với tốc độ bất thường. Điều phối lực lượng bảo vệ ngay.",
                AlertSeverity.Critical
            ),
            (
                "Chuyển động bất thường trong hầm",
                "Camera phát hiện chuyển động bất thường tại khu vực giám sát. Đối tượng không rõ danh tính đang di chuyển trong khu vực hạn chế.",
                AlertSeverity.High
            ),
            (
                "Phát hiện phương tiện không được phép",
                "Hệ thống nhận diện biển số phát hiện phương tiện lạ không có trong danh sách được phép. Phương tiện đang đỗ tại vị trí không quy định.",
                AlertSeverity.High
            ),
            (
                "Phát hiện vật thể bỏ lại",
                "Camera AI phát hiện vật thể bỏ lại trong khu vực hầm hơn 5 phút. Kiểm tra ngay để đảm bảo an toàn theo quy trình.",
                AlertSeverity.Medium
            ),
            (
                "Lảng vảng khu vực hạn chế",
                "Cảnh báo: đối tượng di chuyển quanh khu vực hạn chế trong thời gian dài bất thường. Hành vi đáng ngờ — cần xác minh ngay.",
                AlertSeverity.Medium
            ),
            (
                "Phát hiện nhóm người bất thường",
                "Camera phát hiện tập hợp nhóm người tại khu vực không được phép. Số lượng vượt giới hạn cho phép theo quy định an toàn.",
                AlertSeverity.High
            ),
            (
                "Cảnh báo vượt ranh giới ảo",
                "Hệ thống AI xác nhận có đối tượng vượt qua đường ranh giới giám sát tại vị trí cửa hầm phụ. Kích hoạt quy trình kiểm soát.",
                AlertSeverity.Critical
            ),
            (
                "Mất tín hiệu camera tạm thời",
                "Camera bị mất tín hiệu trong 3 giây. Tín hiệu đã khôi phục nhưng cần kiểm tra thiết bị và đường truyền để xác nhận nguyên nhân.",
                AlertSeverity.Low
            ),
        };
    }
}
