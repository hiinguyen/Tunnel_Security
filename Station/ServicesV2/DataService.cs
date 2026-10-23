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

            var nodes = nodeDtos.Select(n => new TunnelNode
            {
                NodeId = n.Id,
                NodeName = n.Name,
                LineId = DefaultLineId,
                LineName = DefaultLineName
            }).ToList();

            var nodeIndex = nodes.ToDictionary(n => n.NodeId, StringComparer.OrdinalIgnoreCase);

            var sensors = sensorDtos.Select(s => MapSensor(s, nodeIndex)).ToList();
            var cameras = cameraDtos.Select(c => MapCamera(c, nodeIndex)).ToList();

            _nodes = nodes;
            _lines = new List<TunnelLine>
            {
                new TunnelLine { LineId = DefaultLineId, LineName = DefaultLineName, Nodes = nodes }
            };
            _sensors = sensors;
            _cameras = cameras;

            _sensorMap.Clear();
            foreach (var sensor in sensors) _sensorMap[sensor.SensorId] = sensor;

            System.Diagnostics.Debug.WriteLine(
                $"[DataService] Loaded {nodes.Count} nodes, {sensors.Count} sensors, {cameras.Count} cameras from BackendV2");
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

        private static SimulatedCamera MapCamera(CameraDto c, Dictionary<string, TunnelNode> nodeIndex)
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
                StreamUrl = string.IsNullOrWhiteSpace(c.StreamUrl) ? null : c.StreamUrl,
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
            var alert = new Alert
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = DateTimeOffset.Now,
                Severity = severity,
                Category = sensor.Category,
                NodeId = sensor.NodeId,
                NodeName = sensor.NodeName,
                SensorId = sensor.SensorId,
                SensorName = sensor.SensorName,
                SensorValue = sensor.CurrentValue,
                SensorUnit = sensor.Unit,
                Threshold = sensor.WarnThreshold,
                Title = $"Cảnh báo {sensor.SensorName}",
                Description = $"{sensor.SensorName} = {sensor.CurrentValue:F1} {sensor.Unit} (ngưỡng: {sensor.WarnThreshold})",
                State = AlertState.Unprocessed
            };

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
