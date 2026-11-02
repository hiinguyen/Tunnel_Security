using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Station.Models;
using Station.Services;

namespace Station.ServicesV2
{
    /// <summary>
    /// IDataService implementation kết nối BackendV2 (REST /api/Node,/api/Sensor,/api/Camera
    /// + SignalR /hubs/sensors). Thay thế hoàn toàn RealDataService (Backend v1) theo quyết
    /// định kiến trúc — xem DataServiceLocator.
    ///
    /// Ghi chú giới hạn đã biết:
    /// - BackendV2 chưa có khái niệm "Line" (Node.cs không có LineId/LineName) nên toàn bộ
    ///   node được gom vào 1 TunnelLine mặc định (<see cref="DefaultLineId"/>).
    /// - BackendV2 chưa có luồng device-join (JOIN_REQUEST) như Backend v1 — các API
    ///   GetPendingJoinRequestsAsync/ApproveJoinRequestAsync/RejectJoinRequestAsync luôn
    ///   trả về rỗng/false, event NewJoinRequest không bao giờ raise.
    /// </summary>
    public class DataService : IDataService, IAsyncDisposable
    {
        private const string DefaultLineId = "LINE-DEFAULT";
        private const string DefaultLineName = "Tất cả node";

        private readonly string _baseUrl;
        private readonly ApiClient _apiClient;
        private readonly HubClient _hubClient;
        private readonly DispatcherQueue? _dispatcherQueue;

        private readonly Dictionary<string, SimulatedSensor> _sensorMap = new();

        // Cảm biến đang có cảnh báo giả chưa kết thúc (sensorId -> mức), để không phát lặp mỗi mẫu đo.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AlertSeverity> _openAlertSeverities = new();

        private List<SimulatedSensor> _sensors = new();
        private List<SimulatedCamera> _cameras = new();
        private List<TunnelNode> _nodes = new();
        private List<TunnelLine> _lines = new();

        public IReadOnlyList<SimulatedSensor> Sensors => _sensors;
        public IReadOnlyList<SimulatedCamera> Cameras => _cameras;
        public IReadOnlyList<TunnelNode> Nodes => _nodes;
        public IReadOnlyList<TunnelLine> Lines => _lines;
        public ObservableCollection<Alert> ActiveAlerts { get; } = new();
        public ObservableCollection<Alert> AlertHistory { get; } = new();

        public event EventHandler<SensorTickEventArgs>? SensorTick;
        public event EventHandler<AlertGeneratedEventArgs>? AlertGenerated;
        public event EventHandler? TopologyLoaded;
#pragma warning disable CS0067 // BackendV2 chưa hỗ trợ device-join flow — event không bao giờ raise.
        public event EventHandler<JoinRequestNotification>? NewJoinRequest;
#pragma warning restore CS0067

        public DataService()
        {
            _baseUrl = Environment.GetEnvironmentVariable("BACKENDV2_BASE_URL") ?? "http://localhost:5080";
            _apiClient = new ApiClient(_baseUrl);
            _hubClient = new HubClient(_baseUrl, () => _apiClient.AccessToken);
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        public void Start()
        {
            _ = Task.Run(InitializeAsync);
        }

        public void Stop()
        {
            _ = _hubClient.DisposeAsync().AsTask();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await AuthenticateAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DataService] Auth login failed: {ex.Message} — requests to BackendV2 will be unauthorized");
            }

            try
            {
                await LoadTopologyWithRetryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DataService] Topology load failed: {ex.Message} — continuing with empty topology");
            }

            _dispatcherQueue?.TryEnqueue(() => TopologyLoaded?.Invoke(this, EventArgs.Empty));

            try
            {
                await ConnectHubAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataService] Hub connect failed: {ex.Message}");
            }
        }

        // TODO: chuyển sang màn hình đăng nhập thực sự khi có UI — tạm thời đọc credential
        // từ biến môi trường, mặc định khớp tài khoản admin BackendV2 seed sẵn lúc khởi động
        // (xem BackendV2/Data/AuthSeeder.cs + appsettings "Auth:BootstrapAdminPassword").
        private async Task AuthenticateAsync()
        {
            var username = Environment.GetEnvironmentVariable("BACKENDV2_USERNAME") ?? "admin";
            var password = Environment.GetEnvironmentVariable("BACKENDV2_PASSWORD") ?? "ChangeMe123!";
            await _apiClient.LoginAsync(username, password);
        }

        private async Task LoadTopologyWithRetryAsync()
        {
            const int maxAttempts = 5;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await LoadTopologyAsync();
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == maxAttempts) throw;

                    System.Diagnostics.Debug.WriteLine(
                        $"[DataService] Topology load attempt {attempt}/{maxAttempts} failed: {ex.Message}");

                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }

        private async Task LoadTopologyAsync()
        {
            var nodeDtos = await _apiClient.GetNodesAsync();
            var sensorDtos = await _apiClient.GetSensorsAsync();
            var cameraDtos = await _apiClient.GetCamerasAsync();

            // Trạm hub không có cảm biến/camera nên không đưa vào danh sách node giám sát (giống mock).
            var nodes = nodeDtos.Where(n => !n.IsHub).Select(n =>
            {
                var (lineId, lineName) = ResolveLine(n.Code);
                return new TunnelNode
                {
                    NodeId = n.Id,
                    NodeName = n.Name,
                    LineId = lineId,
                    LineName = lineName
                };
            }).ToList();

            var nodeIndex = nodes.ToDictionary(n => n.NodeId, StringComparer.OrdinalIgnoreCase);

            var sensors = sensorDtos.Select(s => MapSensor(s, nodeIndex)).ToList();
            var cameras = cameraDtos.Select(c => MapCamera(c, nodeIndex)).ToList();

            _nodes = nodes;
            _lines = nodes
                .GroupBy(n => (n.LineId, n.LineName))
                .Select(g => new TunnelLine
                {
                    LineId = g.Key.LineId,
                    LineName = g.Key.LineName,
                    Nodes = g.ToList()
                })
                .ToList();
            _sensors = sensors;
            _cameras = cameras;

            _sensorMap.Clear();
            foreach (var sensor in sensors) _sensorMap[sensor.SensorId] = sensor;

            System.Diagnostics.Debug.WriteLine(
                $"[DataService] Loaded {nodes.Count} nodes, {sensors.Count} sensors, {cameras.Count} cameras from BackendV2");
        }

        // Backend chưa có tên tuyến cho node hiển thị; tuyến suy ra từ tiền tố mã node (DC-01, KM-03...)
        // theo cùng bảng tuyến với bản đồ dashboard (nodes.json) và MockDataService.
        private static readonly Dictionary<string, (string Id, string Name)> LinesByCodePrefix =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["DC"]  = ("LINE-DC",  "Tuyến Đội Cấn"),
                ["KM"]  = ("LINE-KM",  "Tuyến Kim Mã"),
                ["HHT"] = ("LINE-HHT", "Tuyến Hoàng Hoa Thám"),
                ["PDP"] = ("LINE-PDP", "Tuyến Phan Đình Phùng")
            };

        private static (string Id, string Name) ResolveLine(string? nodeCode)
        {
            var prefix = nodeCode?.Split('-')[0];
            return prefix != null && LinesByCodePrefix.TryGetValue(prefix, out var line)
                ? line
                : (DefaultLineId, DefaultLineName);
        }

        private static SimulatedSensor MapSensor(SensorDto s, Dictionary<string, TunnelNode> nodeIndex)
        {
            nodeIndex.TryGetValue(s.NodeId, out var node);
            var warn = s.WarningThreshold ?? 30.0;
            var crit = s.CriticalThreshold ?? 50.0;

            return new SimulatedSensor
            {
                SensorId = s.Id,
                SensorName = s.Name,
                Unit = s.Unit,
                NodeId = s.NodeId,
                NodeName = node?.NodeName ?? s.NodeId,
                LineId = node?.LineId ?? DefaultLineId,
                LineName = node?.LineName ?? DefaultLineName,
                Location = node?.NodeName ?? s.NodeId,
                Category = MapCategory(s.Type),
                CurrentValue = s.CurrentValue ?? warn * 0.5,
                NominalValue = warn * 0.5,
                WarnThreshold = warn,
                CriticalThreshold = crit,
                DriftSpeed = (crit - warn) * 0.1,
                AbsoluteMin = 0,
                AbsoluteMax = crit * 1.5,
                MinNormal = 0,
                MaxNormal = warn,
                // Lạc quan online khi mới load — hub sẽ hạ xuống offline nếu mất kết nối
                // (SensorDto.IsActive hiện luôn deserialize về false do lệch tên field JSON).
                IsOnline = true
            };
        }

        // BackendV2.Models.SensorType: 0=Radar,1=Vibration,2=SmokeFire,3=Temperature,
        // 4=Humidity,5=Gas,6=Pressure,7=WaterLevel,8=Motion,9=Light — không có tương ứng 1-1 cho
        // SmokeFire/Gas/Pressure trong AlertCategory nên gộp vào Other.
        private static AlertCategory MapCategory(int sensorType) => sensorType switch
        {
            0 => AlertCategory.Radar,
            1 => AlertCategory.Accelerometer,
            3 => AlertCategory.Temperature,
            4 => AlertCategory.Humidity,
            7 => AlertCategory.WaterLevel,
            8 => AlertCategory.Infrared,
            9 => AlertCategory.Light,
            _ => AlertCategory.Other
        };

        private SimulatedCamera MapCamera(CameraDto c, Dictionary<string, TunnelNode> nodeIndex)
        {
            nodeIndex.TryGetValue(c.NodeId, out var node);

            return new SimulatedCamera
            {
                CameraId = c.Id,
                CameraName = c.CameraName,
                Location = node?.NodeName ?? c.NodeId,
                NodeId = c.NodeId,
                NodeName = node?.NodeName ?? c.NodeId,
                LineId = node?.LineId ?? DefaultLineId,
                LineName = node?.LineName ?? DefaultLineName,
                StreamUrl = ToAbsoluteStreamUrl(c.StreamUrl),
                Description = c.Description,
                Resolution = c.Resolution,
                Fps = c.Fps,
                Codec = c.Codec,
                IrEnabled = c.IrEnabled,
                HdrEnabled = c.HdrEnabled,
                IsRecording = c.IsRecording,
                LastFrameTime = c.LastFrameTime,
                // Lạc quan online khi mới load (CameraDto.IsOnline/CameraName hiện luôn sai
                // do lệch tên field JSON — xem ghi chú đầu file).
                IsOnline = true,
                Status = 0
            };
        }

        /// <summary>BackendV2 lưu đường dẫn WS tương đối để không phụ thuộc địa chỉ PC; ghép với địa chỉ API đang dùng.</summary>
        private string? ToAbsoluteStreamUrl(string? streamUrl)
        {
            if (string.IsNullOrWhiteSpace(streamUrl)) return null;
            if (!streamUrl.StartsWith('/')) return streamUrl;
            var api = new Uri(_baseUrl);
            var scheme = api.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            return $"{scheme}://{api.Authority}{streamUrl}";
        }

        private async Task ConnectHubAsync()
        {
            _hubClient.ReadingReceived += OnReadingReceived;
            _hubClient.ConnectionChanged += OnConnectionChanged;
            await _hubClient.ConnectAsync();
        }

        private void OnReadingReceived(object? sender, ReadingDto reading)
        {
            if (!_sensorMap.TryGetValue(reading.SensorId, out var sensor))
            {
                return; // Sensor chưa có trong topology hiện tại — bỏ qua.
            }

            sensor.CurrentValue = reading.Value;
            sensor.IsOnline = true;

            var isAnomaly = sensor.CurrentLevel >= SensorAlertLevel.Warning;
            var args = new SensorTickEventArgs
            {
                Sensor = sensor,
                NewValue = reading.Value,
                Timestamp = new DateTimeOffset(reading.Timestamp, TimeSpan.Zero),
                IsAnomaly = isAnomaly
            };

            if (_dispatcherQueue != null)
                _dispatcherQueue.TryEnqueue(() => SensorTick?.Invoke(this, args));
            else
                SensorTick?.Invoke(this, args);

            if (isAnomaly) TryGenerateAlert(sensor);
            else _openAlertSeverities.TryRemove(sensor.SensorId, out _);
        }

        private void OnConnectionChanged(object? sender, bool connected)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] Hub connected: {connected}");
            if (!connected)
            {
                foreach (var s in _sensors) s.IsOnline = false;
            }
        }

        private void TryGenerateAlert(SimulatedSensor sensor)
        {
            var severity = sensor.CurrentAlertSeverity;
            // Camera đặt tại cùng node với cảm biến, dùng để xem hiện trường khi cảnh báo.
            var cameraId = _cameras.FirstOrDefault(c => string.Equals(c.NodeId, sensor.NodeId, StringComparison.OrdinalIgnoreCase))?.CameraId;
            var alert = new Alert
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = DateTimeOffset.Now,
                Severity = severity,
                Category = sensor.Category,
                NodeId = sensor.NodeId,
                NodeName = sensor.NodeName,
                CameraId = cameraId,
                SensorId = sensor.SensorId,
                SensorName = sensor.SensorName,
                SensorValue = sensor.CurrentValue,
                SensorUnit = sensor.Unit,
                Threshold = sensor.WarnThreshold,
                Title = $"Cảnh báo {sensor.SensorName}",
                Description = $"{sensor.SensorName} = {sensor.CurrentValue:F1} {sensor.Unit} (ngưỡng: {sensor.WarnThreshold})",
                State = AlertState.Unprocessed
            };

            // Giá trị vượt ngưỡng tự nhiên của mô phỏng không phải cảnh báo: chỉ ghi nhận khi
            // AlertInjector đang ép giá trị cho đúng node + cảm biến này.
            if (!InjectedAlertGate.Shared.IsActive(alert))
            {
                _openAlertSeverities.TryRemove(sensor.SensorId, out _);
                return;
            }

            // Mỗi lần bơm chỉ tạo một cảnh báo; mẫu đo kế tiếp cùng mức bị bỏ qua, đổi mức thì tạo mới.
            if (_openAlertSeverities.TryGetValue(sensor.SensorId, out var openSeverity) && openSeverity == severity) return;
            _openAlertSeverities[sensor.SensorId] = severity;

            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    ActiveAlerts.Insert(0, alert);
                    AlertHistory.Insert(0, alert);
                    if (ActiveAlerts.Count > 100) ActiveAlerts.RemoveAt(ActiveAlerts.Count - 1);
                    AlertGenerated?.Invoke(this, new AlertGeneratedEventArgs { Alert = alert });
                });
            }
        }

        public Task<IReadOnlyList<JoinRequestNotification>> GetPendingJoinRequestsAsync()
            => Task.FromResult<IReadOnlyList<JoinRequestNotification>>(Array.Empty<JoinRequestNotification>());

        public Task<bool> ApproveJoinRequestAsync(int requestId, byte nodeByteId)
        {
            System.Diagnostics.Debug.WriteLine(
                "[DataService] ApproveJoinRequestAsync: BackendV2 chưa hỗ trợ device-join flow.");
            return Task.FromResult(false);
        }

        public Task<bool> RejectJoinRequestAsync(int requestId, string? reason = null)
        {
            System.Diagnostics.Debug.WriteLine(
                "[DataService] RejectJoinRequestAsync: BackendV2 chưa hỗ trợ device-join flow.");
            return Task.FromResult(false);
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            await _hubClient.DisposeAsync();
        }
    }
}
