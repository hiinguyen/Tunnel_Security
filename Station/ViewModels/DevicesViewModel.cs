using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Station.Models;
using Station.Services;
using System.Threading.Tasks;
using Windows.UI;

namespace Station.ViewModels
{
    public enum DeviceSidebarMode
    {
        Summary,
        Details,
        Edit
    }

    public partial class DevicesViewModel : ObservableObject
    {
        private readonly IDataService _dataService = DataServiceLocator.Current;
        private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Stores user-added nodes so they survive filter changes
        private readonly Dictionary<string, NodeItemViewModel> _userAddedNodes = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSidebarSummaryVisible))]
        [NotifyPropertyChangedFor(nameof(IsSidebarDetailVisible))]
        [NotifyPropertyChangedFor(nameof(IsSidebarEditVisible))]
        private DeviceSidebarMode _sidebarMode = DeviceSidebarMode.Summary;

        public bool IsSidebarSummaryVisible => SidebarMode == DeviceSidebarMode.Summary;
        public bool IsSidebarDetailVisible => SidebarMode == DeviceSidebarMode.Details;
        public bool IsSidebarEditVisible => SidebarMode == DeviceSidebarMode.Edit;

        // Filter properties
        private string? _selectedStatus = "Tất cả trạng thái";
        public string? SelectedStatus
        {
            get => _selectedStatus;
            set
            {
                SetProperty(ref _selectedStatus, value);
                ApplyFilters();
            }
        }

        [ObservableProperty]
        private SensorItemViewModel? _selectedSensor;

        partial void OnSelectedSensorChanged(SensorItemViewModel? value)
        {
            if (value != null)
            {
                SidebarMode = DeviceSidebarMode.Summary;
            }
        }

        [ObservableProperty]
        private NodeItemViewModel? _selectedNode;

        partial void OnSelectedNodeChanged(NodeItemViewModel? value)
        {
            if (value != null)
            {
                SidebarMode = DeviceSidebarMode.Summary;
            }
        }

        private string? _selectedLine = "Tất cả tuyến";
        public string? SelectedLine
        {
            get => _selectedLine;
            set
            {
                SetProperty(ref _selectedLine, value);
                ApplyFilters();
            }
        }

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                SetProperty(ref _searchText, value);
                ApplyFilters();
            }
        }

        // Device collections
        public ObservableCollection<DeviceItemViewModel> AllDevices { get; } = new();
        public ObservableCollection<DeviceItemViewModel> FilteredDevices { get; } = new();
        public ObservableCollection<NodeItemViewModel> FilteredNodes { get; } = new();
        public ObservableCollection<NodeItemViewModel> PagedFilteredNodes { get; } = new();
        public ObservableCollection<string> StatusFilters { get; } = new();
        public ObservableCollection<string> LineFilters { get; } = new();

        // Pagination
        private const int PageSize = 8;
        private int _currentPage = 1;
        public int CurrentPage => _currentPage;
        public int TotalPages => Math.Max(1, (int)Math.Ceiling(FilteredNodes.Count / (double)PageSize));
        public bool CanGoPrevPage => _currentPage > 1;
        public bool CanGoNextPage => _currentPage < TotalPages;
        public string PageFooterText => FilteredNodes.Count == 0
            ? "Không có node nào"
            : $"Hiển thị {(_currentPage - 1) * PageSize + 1}-{Math.Min(_currentPage * PageSize, FilteredNodes.Count)} trong {FilteredNodes.Count} node";

        [RelayCommand]
        private void PrevPage()
        {
            if (!CanGoPrevPage) return;
            _currentPage--;
            NotifyPaginationChanged();
            RefreshPagedNodes();
        }

        [RelayCommand]
        private void NextPage()
        {
            if (!CanGoNextPage) return;
            _currentPage++;
            NotifyPaginationChanged();
            RefreshPagedNodes();
        }

        private void NotifyPaginationChanged()
        {
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(CanGoPrevPage));
            OnPropertyChanged(nameof(CanGoNextPage));
            OnPropertyChanged(nameof(PageFooterText));
            OnPropertyChanged(nameof(NoNodesVisibility));
        }

        private void RefreshPagedNodes()
        {
            PagedFilteredNodes.Clear();
            var skip = (_currentPage - 1) * PageSize;
            foreach (var node in FilteredNodes.Skip(skip).Take(PageSize))
                PagedFilteredNodes.Add(node);
        }

        // Pending join requests from hardware devices
        public ObservableCollection<JoinRequestItemViewModel> PendingJoinRequests { get; } = new();

        private int _pendingJoinCount;
        public int PendingJoinCount
        {
            get => _pendingJoinCount;
            set
            {
                SetProperty(ref _pendingJoinCount, value);
                OnPropertyChanged(nameof(HasPendingJoins));
                OnPropertyChanged(nameof(PendingJoinSectionVisibility));
                OnPropertyChanged(nameof(NoPendingJoinVisibility));
                OnPropertyChanged(nameof(PendingJoinFooterText));
            }
        }
        public bool HasPendingJoins => PendingJoinCount > 0;
        public Visibility PendingJoinSectionVisibility => PendingJoinCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoPendingJoinVisibility => PendingJoinCount > 0 ? Visibility.Collapsed : Visibility.Visible;
        public Visibility NoNodesVisibility => FilteredNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public string PendingJoinFooterText => $"Hiển thị {Math.Min(PendingJoinCount, 12)} trên {PendingJoinCount} yêu cầu đang chờ";

        private DateTimeOffset _pendingJoinLastUpdatedAt;
        public string PendingJoinLastUpdatedText =>
            _pendingJoinLastUpdatedAt == default ? "--:--:--" : _pendingJoinLastUpdatedAt.ToLocalTime().ToString("HH:mm:ss");
        public string PendingJoinLastUpdatedDisplay => $"Cập nhật: {PendingJoinLastUpdatedText}";

        // Statistics
        private int _totalDevices;
        public int TotalDevices
        {
            get => _totalDevices;
            set => SetProperty(ref _totalDevices, value);
        }

        private int _onlineDevices;
        public int OnlineDevices
        {
            get => _onlineDevices;
            set => SetProperty(ref _onlineDevices, value);
        }

        private int _offlineDevices;
        public int OfflineDevices
        {
            get => _offlineDevices;
            set => SetProperty(ref _offlineDevices, value);
        }

        private int _faultDevices;
        public int FaultDevices
        {
            get => _faultDevices;
            set => SetProperty(ref _faultDevices, value);
        }

        public DevicesViewModel()
        {
            _dataService.SensorTick     += OnSensorTick;
            _dataService.TopologyLoaded += OnTopologyLoaded;
            _dataService.NewJoinRequest += OnNewJoinRequest;

            // Load once after subscriptions are in place so we don't miss an early
            // TopologyLoaded event from the background data service.
            ReloadFromDataService();
            _ = LoadPendingJoinRequestsAsync();
        }

        private void OnNewJoinRequest(object? sender, Station.Services.JoinRequestNotification req)
        {
            void AddRequest() => UpsertPendingJoin(req);

            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(AddRequest);
            else
                AddRequest();
        }

        private async Task LoadPendingJoinRequestsAsync()
        {
            try
            {
                var pending = await _dataService.GetPendingJoinRequestsAsync();
                void Apply()
                {
                    PendingJoinRequests.Clear();
                    foreach (var req in pending
                                 .OrderBy(r => ParseRequestedAtOrMin(r.RequestedAt)))
                    {
                        UpsertPendingJoin(req, updateTimestamp: false);
                    }
                    PendingJoinCount = PendingJoinRequests.Count;
                    TouchPendingJoinUpdatedTime();
                }

                if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                    _dispatcher.TryEnqueue(Apply);
                else
                    Apply();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DevicesVM] LoadPendingJoinRequests failed: {ex.Message}");
            }
        }

        private static DateTimeOffset ParseRequestedAtOrMin(string value)
        {
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                ? dt
                : DateTimeOffset.MinValue;
        }

        private void UpsertPendingJoin(JoinRequestNotification req, bool updateTimestamp = true)
        {
            var existing = PendingJoinRequests.FirstOrDefault(r => r.Id == req.Id);
            if (existing != null)
                PendingJoinRequests.Remove(existing);

            PendingJoinRequests.Insert(0, new JoinRequestItemViewModel
            {
                Id              = req.Id,
                MacAddress      = req.MacAddress,
                HardwareId      = req.HardwareId,
                FirmwareVersion = req.FirmwareVersion,
                RequestedAt     = req.RequestedAt,
                NodeByteIdInput = ((req.Id % 250) + 1).ToString(),
                ViewModel       = this
            });

            PendingJoinCount = PendingJoinRequests.Count;
            if (updateTimestamp)
                TouchPendingJoinUpdatedTime();
        }

        private void TouchPendingJoinUpdatedTime()
        {
            _pendingJoinLastUpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(PendingJoinLastUpdatedText));
            OnPropertyChanged(nameof(PendingJoinLastUpdatedDisplay));
        }

        internal void RemovePendingJoin(int requestId)
        {
            var item = PendingJoinRequests.FirstOrDefault(r => r.Id == requestId);
            if (item == null) return;
            PendingJoinRequests.Remove(item);
            PendingJoinCount = PendingJoinRequests.Count;
            TouchPendingJoinUpdatedTime();
        }

        internal async Task<bool> ApproveJoinAsync(int requestId, byte nodeByteId)
            => await _dataService.ApproveJoinRequestAsync(requestId, nodeByteId);

        internal async Task<bool> RejectJoinAsync(int requestId)
            => await _dataService.RejectJoinRequestAsync(requestId);

        private void OnTopologyLoaded(object? sender, EventArgs e)
        {
            if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                _dispatcher.TryEnqueue(ReloadFromDataService);
            else
                ReloadFromDataService();
        }

        private void ReloadFromDataService()
        {
            AllDevices.Clear();
            LineFilters.Clear();
            StatusFilters.Clear();

            LoadFromDataService();
            ApplyFilters();
            UpdateStatistics();
        }

        private void OnSensorTick(object? sender, SensorTickEventArgs e)
        {
            // Update device value in real-time
            try
            {
                // Find sensor in current data service to get updated value
                var sensor = _dataService.Sensors.FirstOrDefault(s => s.SensorId == e.Sensor.SensorId);
                if (sensor == null) return;

                // Find matching device in our list
                var device = AllDevices.FirstOrDefault(d => d.DeviceId == e.Sensor.SensorId);
                if (device != null)
                {
                    // Update status based on sensor level
                    device.Status = sensor.CurrentLevel switch
                    {
                        SensorAlertLevel.Critical => DeviceStatus.Fault,
                        SensorAlertLevel.Warning => DeviceStatus.Online,
                        SensorAlertLevel.Offline => DeviceStatus.Offline,
                        _ => DeviceStatus.Online
                    };

                    device.LastOnline = DateTimeOffset.Now;
                }

                // Update the filtered nodes with new sensor values
                RefreshNodeSensorValues(e.Sensor.SensorId, sensor.CurrentValue, sensor.Category, sensor.CurrentLevel);

                // Update statistics
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DevicesVM] Error updating sensor: {ex.Message}");
            }
        }

        private void RefreshNodeSensorValues(string sensorId, double newValue, AlertCategory category, SensorAlertLevel level)
        {
            // Update sensor value in NodeItemViewModel
            foreach (var node in FilteredNodes)
            {
                foreach (var sensor in node.Sensors)
                {
                    if (sensor.SensorId == sensorId)
                    {
                        // Format value based on category
                        sensor.CurrentValue = category switch
                        {
                            AlertCategory.Temperature => $"{newValue:F1}°C",
                            AlertCategory.Humidity => $"{newValue:F1}%RH",
                            AlertCategory.Radar => $"{newValue:F0}%",
                            AlertCategory.Infrared => $"{newValue:F0}%",
                            AlertCategory.Light => $"{newValue:F0} lux",
                            AlertCategory.Accelerometer => $"{newValue:F2} m/s²",
                            _ => $"{newValue:F2}"
                        };

                        // Update status
                        sensor.SensorStatus = level switch
                        {
                            SensorAlertLevel.Critical => DeviceStatus.Fault,
                            SensorAlertLevel.Warning => DeviceStatus.Online,
                            SensorAlertLevel.Offline => DeviceStatus.Offline,
                            _ => DeviceStatus.Online
                        };
                        break;
                    }
                }
            }
        }

        private void LoadFromDataService()
        {
            StatusFilters.Add("Tất cả trạng thái");
            StatusFilters.Add("Hoạt động");
            StatusFilters.Add("Ngoại tuyến");
            StatusFilters.Add("Lỗi");
            StatusFilters.Add("Tắt");

            LineFilters.Add("Tất cả tuyến");
            foreach (var line in _dataService.Lines)
                LineFilters.Add(line.LineName);

            // Add cameras
            foreach (var cam in _dataService.Cameras)
            {
                AllDevices.Add(new DeviceItemViewModel
                {
                    Name            = cam.CameraName,
                    DeviceId        = cam.CameraId,
                    Type            = "Camera",
                    TypeDisplay     = "Camera giám sát",
                    Location        = $"{cam.LineName} / {cam.NodeName}",
                    IpAddress       = string.Empty,
                    Status          = cam.IsOnline ? DeviceStatus.Online : DeviceStatus.Offline,
                    LastOnline      = DateTimeOffset.Now.AddMinutes(-1),
                    Manufacturer    = "Hikvision",
                    FirmwareVersion = "V5.7.3",
                    AlertCount      = 0
                });
            }

            // Add sensors
            foreach (var s in _dataService.Sensors)
            {
                AllDevices.Add(new DeviceItemViewModel
                {
                    Name            = s.SensorName,
                    DeviceId        = s.SensorId,
                    Type            = "Sensor",
                    TypeDisplay     = CategoryToDisplay(s.Category),
                    Location        = $"{s.LineName} / {s.NodeName}",
                    IpAddress       = string.Empty,
                    Status          = s.IsOnline ? DeviceStatus.Online : DeviceStatus.Offline,
                    LastOnline      = DateTimeOffset.Now.AddSeconds(-5),
                    Manufacturer    = "Bosch",
                    FirmwareVersion = "V3.1.0",
                    AlertCount      = 0
                });
            }

            TotalDevices = AllDevices.Count;
        }

        private static string CategoryToDisplay(Station.Models.AlertCategory cat) => cat switch
        {
            Station.Models.AlertCategory.Radar         => "Radar phát hiện người",
            Station.Models.AlertCategory.Infrared      => "Cảm biến hồng ngoại",
            Station.Models.AlertCategory.Temperature   => "Cảm biến nhiệt độ",
            Station.Models.AlertCategory.Humidity      => "Cảm biến độ ẩm",
            Station.Models.AlertCategory.Light         => "Cảm biến ánh sáng",
            Station.Models.AlertCategory.Accelerometer => "Cảm biến gia tốc",
            _                                          => "Cảm biến"
        };

        private static string FormatSensorValue(Station.Services.SimulatedSensor s) =>
            s.Category switch
            {
                Station.Models.AlertCategory.Radar         => $"{s.CurrentValue:F0}%",
                Station.Models.AlertCategory.Infrared      => $"{s.CurrentValue:F0}%",
                Station.Models.AlertCategory.Temperature   => $"{s.CurrentValue:F1}°C",
                Station.Models.AlertCategory.Humidity      => $"{s.CurrentValue:F1}%RH",
                Station.Models.AlertCategory.Light         => $"{s.CurrentValue:F0} lux",
                Station.Models.AlertCategory.Accelerometer => $"{s.CurrentValue:F2} m/s²",
                _ => $"{s.CurrentValue:F2}"
            };

        private static string CategoryIcon(Station.Models.AlertCategory cat) => cat switch
        {
            Station.Models.AlertCategory.Radar         => "\uE701",
            Station.Models.AlertCategory.Infrared      => "\uE7C1",
            Station.Models.AlertCategory.Temperature   => "\uE9CA",
            Station.Models.AlertCategory.Humidity      => "\uE81E",
            Station.Models.AlertCategory.Light         => "\uE706",
            Station.Models.AlertCategory.Accelerometer => "\uEDA4",
            _                                          => "\uE957"
        };

        private void ApplyFilters()
        {
            FilteredDevices.Clear();
            FilteredNodes.Clear();

            var filtered = AllDevices.AsEnumerable();

            // Filter by status
            if (!string.IsNullOrEmpty(SelectedStatus) && SelectedStatus != "Tất cả trạng thái")
            {
                filtered = filtered.Where(d =>
                {
                    return SelectedStatus switch
                    {
                        "Hoạt động" => d.Status == DeviceStatus.Online,
                        "Ngoại tuyến" => d.Status == DeviceStatus.Offline,
                        "Lỗi" => d.Status == DeviceStatus.Fault,
                        "Tắt" => d.Status == DeviceStatus.Disabled,
                        _ => true
                    };
                });
            }

            // Filter by line
            if (!string.IsNullOrEmpty(SelectedLine) && SelectedLine != "Tất cả tuyến")
            {
                filtered = filtered.Where(d => d.Location.StartsWith(SelectedLine));
            }

            // Filter by search text
            if (!string.IsNullOrEmpty(SearchText))
            {
                var searchLower = SearchText.ToLower();
                filtered = filtered.Where(d =>
              d.Name.ToLower().Contains(searchLower) ||
               d.DeviceId.ToLower().Contains(searchLower) ||
                       d.Location.ToLower().Contains(searchLower) ||
                  d.IpAddress.ToLower().Contains(searchLower));
            }

            foreach (var device in filtered)
            {
                FilteredDevices.Add(device);
            }

            foreach (var node in _dataService.Nodes)
            {
                var nodeDevices = _dataService.Sensors.Where(s => s.NodeId == node.NodeId).ToList();
                var nodeCam = _dataService.Cameras.FirstOrDefault(c => c.NodeId == node.NodeId);

                var status = DeviceStatus.Online;
                if ((nodeCam != null && !nodeCam.IsOnline) || nodeDevices.Any(d => !d.IsOnline))
                {
                    status = DeviceStatus.Offline;
                }

                if (nodeDevices.Any(d => d.CurrentLevel == SensorAlertLevel.Critical))
                    status = DeviceStatus.Fault;
                else if (status == DeviceStatus.Online && nodeDevices.Any(d => d.CurrentLevel == SensorAlertLevel.Warning))
                    status = DeviceStatus.Fault;

                var nodeVm = new NodeItemViewModel
                {
                    NodeId   = node.NodeId,
                    NodeName = node.NodeName,
                    LineName = node.LineName,
                    Location = $"{node.LineName} / {node.NodeName}",
                    Status   = status
                };

                if (nodeCam != null)
                {
                    nodeVm.Sensors.Add(new SensorItemViewModel
                    {
                        SensorId       = nodeCam.CameraId,
                        SensorName     = nodeCam.CameraName,
                        SensorType     = "Camera",
                        CurrentValue   = nodeCam.IsOnline ? "Online" : "Offline",
                        Unit           = string.Empty,
                        LastUpdateText = "Vừa xong",
                        SensorStatus   = nodeCam.IsOnline ? DeviceStatus.Online : DeviceStatus.Offline,
                        TypeIcon       = "\uE714",
                        LineName       = node.LineName,
                        NodeName       = node.NodeName,
                        Location       = $"{node.LineName} / {node.NodeName}"
                    });
                }

                foreach (var s in nodeDevices)
                {
                    nodeVm.Sensors.Add(new SensorItemViewModel
                    {
                        SensorId       = s.SensorId,
                        SensorName     = s.SensorName,
                        SensorType     = s.Category.ToString(),
                        CurrentValue   = FormatSensorValue(s),
                        Unit           = s.Unit,
                        LastUpdateText = "Vừa xong",
                        SensorStatus   = s.IsOnline ? DeviceStatus.Online : DeviceStatus.Offline,
                        TypeIcon       = CategoryIcon(s.Category),
                        LineName       = node.LineName,
                        NodeName       = node.NodeName,
                        Location       = $"{node.LineName} / {node.NodeName}"
                    });
                }

                FilteredNodes.Add(nodeVm);
            }

            // Re-inject user-added nodes that pass current filters
            foreach (KeyValuePair<string, NodeItemViewModel> entry in _userAddedNodes)
            {
                if (!FilteredNodes.Any(n => n.Location == entry.Key) && NodePassesCurrentFilters(entry.Value))
                    FilteredNodes.Add(entry.Value);
            }

            UpdateStatistics();
            _currentPage = 1;
            NotifyPaginationChanged();
            RefreshPagedNodes();
        }

        private void UpdateStatistics()
        {
            int uOnline  = _userAddedNodes.Values.Sum(n => n.Sensors.Count(s => s.SensorStatus == DeviceStatus.Online));
            int uOffline = _userAddedNodes.Values.Sum(n => n.Sensors.Count(s => s.SensorStatus == DeviceStatus.Offline));
            int uFault   = _userAddedNodes.Values.Sum(n => n.Sensors.Count(s => s.SensorStatus == DeviceStatus.Fault));
            int uTotal   = _userAddedNodes.Values.Sum(n => n.Sensors.Count);

            TotalDevices   = AllDevices.Count + uTotal;
            OnlineDevices  = AllDevices.Count(d => d.Status == DeviceStatus.Online)  + uOnline;
            OfflineDevices = AllDevices.Count(d => d.Status == DeviceStatus.Offline) + uOffline;
            FaultDevices   = AllDevices.Count(d => d.Status == DeviceStatus.Fault)   + uFault;
        }

        // ─── Public CRUD helpers called by dialogs ────────────────────────

        public void RegisterNewNode(NodeItemViewModel node)
        {
            _userAddedNodes[node.Location] = node;
            if (NodePassesCurrentFilters(node))
                FilteredNodes.Add(node);
            UpdateStatistics();
            NotifyPaginationChanged();
            RefreshPagedNodes();
        }

        public void DeleteNode(NodeItemViewModel node)
        {
            _userAddedNodes.Remove(node.Location);
            FilteredNodes.Remove(node);
            UpdateStatistics();
            if (_currentPage > TotalPages) _currentPage = TotalPages;
            NotifyPaginationChanged();
            RefreshPagedNodes();
        }

        public void UpdateNode(NodeItemViewModel node, string newName, string newLineName, string newLocation)
        {
            var oldLocation = node.Location;
            if (_userAddedNodes.Remove(oldLocation))
                _userAddedNodes[newLocation] = node;

            node.NodeName = newName;
            node.LineName = newLineName;
            node.Location = newLocation;
            foreach (var s in node.Sensors)
            {
                s.NodeName = newName;
                s.LineName = newLineName;
                s.Location = newLocation;
            }
        }

        private bool NodePassesCurrentFilters(NodeItemViewModel node)
        {
            if (!string.IsNullOrEmpty(SelectedLine) && SelectedLine != "Tất cả tuyến")
                if (node.LineName != SelectedLine) return false;

            if (!string.IsNullOrEmpty(SelectedStatus) && SelectedStatus != "Tất cả trạng thái")
            {
                bool passes = SelectedStatus switch
                {
                    "Hoạt động"   => node.Status == DeviceStatus.Online,
                    "Ngoại tuyến" => node.Status == DeviceStatus.Offline,
                    "Lỗi"         => node.Status == DeviceStatus.Fault,
                    "Tắt"         => node.Status == DeviceStatus.Disabled,
                    _             => true
                };
                if (!passes) return false;
            }

            if (!string.IsNullOrEmpty(SearchText))
            {
                var sl = SearchText.ToLower();
                if (!node.NodeName.ToLower().Contains(sl) &&
                    !node.Location.ToLower().Contains(sl) &&
                    !node.LineName.ToLower().Contains(sl))
                    return false;
            }
            return true;
        }

        [RelayCommand]
        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        [RelayCommand]
        private void ShowDetails()
        {
            SidebarMode = DeviceSidebarMode.Details;
        }

        [RelayCommand]
        private void ShowEdit()
        {
            SidebarMode = DeviceSidebarMode.Edit;
        }

        [RelayCommand]
        private void ShowSummary()
        {
            SidebarMode = DeviceSidebarMode.Summary;
        }

        [RelayCommand]
        private void CloseSidebar()
        {
            SelectedNode = null;
            SelectedSensor = null;
        }

        [RelayCommand]
        private void NavigateBack()
        {
            SelectedSensor = null;
        }

        [RelayCommand]
        private void AddDevice()
        {
            // Placeholder for Add Device logic
            System.Diagnostics.Debug.WriteLine("Add Device command executed");
        }
    }

    public partial class DeviceItemViewModel : ObservableObject
    {
        private static SolidColorBrush Tb(string key) => (SolidColorBrush)Station.Services.ThemeBrushes.Get(key);

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private string _type = string.Empty;

        [ObservableProperty]
        private string _typeDisplay = string.Empty;

        [ObservableProperty]
        private string _location = string.Empty;

        [ObservableProperty]
        private string _ipAddress = string.Empty;

        [ObservableProperty]
        private DeviceStatus _status;

        [ObservableProperty]
        private DateTimeOffset _lastOnline;

        [ObservableProperty]
        private string _manufacturer = string.Empty;

        [ObservableProperty]
        private string _firmwareVersion = string.Empty;

        [ObservableProperty]
        private int _alertCount;

        public string LastOnlineText
        {
            get
            {
                var diff = DateTimeOffset.Now - LastOnline;
                if (diff.TotalMinutes < 1) return "Vừa xong";
                if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} phút trước";
                if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} giờ trước";
                if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} ngày trước";
                return LastOnline.ToString("dd/MM/yyyy HH:mm");
            }
        }

        // Status badge properties
        public string StatusText
        {
            get => Status switch
            {
                DeviceStatus.Online => "Hoạt động",
                DeviceStatus.Offline => "Ngoại tuyến",
                DeviceStatus.Fault => "Lỗi",
                DeviceStatus.Disabled => "Tắt",
                _ => "Không xác định"
            };
        }

        public SolidColorBrush StatusColor
        {
            get => Status switch
            {
                DeviceStatus.Online   => Tb("DkGreenBrush"),
                DeviceStatus.Offline  => Tb("DkGrayBrush"),
                DeviceStatus.Fault    => Tb("DkRedBrush"),
                DeviceStatus.Disabled => Tb("DkGrayBrush"),
                _ => Tb("DkGrayBrush")
            };
        }

        public SolidColorBrush StatusBackgroundColor
        {
            get => Status switch
            {
                DeviceStatus.Online   => Tb("DkGreenBgBrush"),
                DeviceStatus.Offline  => Tb("DkGrayBgBrush"),
                DeviceStatus.Fault    => Tb("DkRedBgBrush"),
                DeviceStatus.Disabled => Tb("DkGrayBgBrush"),
                _ => Tb("DkGrayBgBrush")
            };
        }

        public string StatusIcon
        {
            get => Status switch
            {
                DeviceStatus.Online => "\uE73E", // Checkmark
                DeviceStatus.Offline => "\uE894", // Disconnect
                DeviceStatus.Fault => "\uE783", // Error
                DeviceStatus.Disabled => "\uE8D8", // Blocked
                _ => "\uE946" // Info
            };
        }

        // Type icon
        public string TypeIcon
        {
            get => Type switch
            {
                "Camera" => "\uE714", // Video camera
                "Sensor" => "\uE957", // Sensor
                "Radar" => "\uE701", // Radar
                _ => "\uE8EA" // Device
            };
        }

        // Alert count display
        public bool HasAlerts => AlertCount > 0;
        public string AlertCountText => AlertCount > 99 ? "99+" : AlertCount.ToString();

        // Device Menu Commands
        [RelayCommand]
        private async Task EditDevice()
        {
            try
            {
                // Create and show the Edit Device Dialog
                var dialog = new Station.Dialogs.EditDeviceDialog(this);

                // Set XamlRoot from App's main window
                if (Microsoft.UI.Xaml.Application.Current is App app && app.m_window is MainWindow mainWindow)
                {
                    dialog.XamlRoot = mainWindow.Content.XamlRoot;
                }

                var result = await dialog.ShowAsync();

                if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    System.Diagnostics.Debug.WriteLine($"Device successfully updated: {Name}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening edit dialog: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ControlDevice()
        {
            try
            {
                // Create and show the Device Control Dialog
                var dialog = new Station.Dialogs.DeviceControlDialog(this);

                // Set XamlRoot from App's main window
                if (Microsoft.UI.Xaml.Application.Current is App app && app.m_window is MainWindow mainWindow)
                {
                    dialog.XamlRoot = mainWindow.Content.XamlRoot;
                }

                var result = await dialog.ShowAsync();

                if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    System.Diagnostics.Debug.WriteLine($"Device control completed: {Name}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening control dialog: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ViewData()
        {
            try
            {
                // Create and show the Edit Device Dialog
                var dialog = new Station.Dialogs.DeviceDataDialog(this);

                // Set XamlRoot from App's main window
                if (Microsoft.UI.Xaml.Application.Current is App app && app.m_window is MainWindow mainWindow)
                {
                    dialog.XamlRoot = mainWindow.Content.XamlRoot;
                }

                var result = await dialog.ShowAsync();

                if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    System.Diagnostics.Debug.WriteLine($"Device successfully updated: {Name}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening edit dialog: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task PlaybackDevice()
        {
            try
            {
                // Create and show the Playback Dialog
                var dialog = new Station.Dialogs.PlaybackDialog(this);

                // Set XamlRoot from App's main window
                if (Microsoft.UI.Xaml.Application.Current is App app && app.m_window is MainWindow mainWindow)
                {
                    dialog.XamlRoot = mainWindow.Content.XamlRoot;
                }

                var result = await dialog.ShowAsync();

                if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    System.Diagnostics.Debug.WriteLine($"Playback completed: {Name}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening playback dialog: {ex.Message}");
            }
        }
    }

    public partial class NodeItemViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _nodeId = string.Empty;

        [ObservableProperty]
        private string _nodeName = string.Empty;

        [ObservableProperty]
        private string _lineName = string.Empty;

        [ObservableProperty]
        private string _location = string.Empty;

        [ObservableProperty]
        private DeviceStatus _status;

        [ObservableProperty]
        private bool _isExpanded = false;

        public ObservableCollection<SensorItemViewModel> Sensors { get; } = new();

        public string StatusText
        {
            get => Status switch
            {
                DeviceStatus.Online => "Hoạt động",
                DeviceStatus.Offline => "Ngoại tuyến",
                DeviceStatus.Fault => "Lỗi",
                DeviceStatus.Disabled => "Tắt",
                _ => "Không xác định"
            };
        }

        // Status palette follows DESIGN.md severity grammar (healthy / critical / offline).
        public SolidColorBrush StatusColor
        {
            get => Status switch
            {
                DeviceStatus.Online => new SolidColorBrush(Color.FromArgb(255, 63, 207, 142)),
                DeviceStatus.Offline => new SolidColorBrush(Color.FromArgb(255, 123, 126, 133)),
                DeviceStatus.Fault => new SolidColorBrush(Color.FromArgb(255, 255, 82, 82)),
                DeviceStatus.Disabled => new SolidColorBrush(Color.FromArgb(255, 100, 116, 139)),
                _ => new SolidColorBrush(Color.FromArgb(255, 123, 126, 133))
            };
        }

        public SolidColorBrush StatusBackgroundColor
        {
            get => Status switch
            {
                DeviceStatus.Online => new SolidColorBrush(Color.FromArgb(40, 63, 207, 142)),
                DeviceStatus.Offline => new SolidColorBrush(Color.FromArgb(40, 123, 126, 133)),
                DeviceStatus.Fault => new SolidColorBrush(Color.FromArgb(40, 255, 82, 82)),
                DeviceStatus.Disabled => new SolidColorBrush(Color.FromArgb(40, 100, 116, 139)),
                _ => new SolidColorBrush(Color.FromArgb(40, 123, 126, 133))
            };
        }

        /// <summary>
        /// Geometric marker so status is readable without color:
        /// filled circle = online, ring = offline, triangle = fault, square = disabled.
        /// </summary>
        public string StatusMarker => Status switch
        {
            DeviceStatus.Online => "●",
            DeviceStatus.Offline => "○",
            DeviceStatus.Fault => "▲",
            DeviceStatus.Disabled => "■",
            _ => "○"
        };

        public string SensorCountText => $"{Sensors.Count} cảm biến";

        // Server data uses "Camera", nodes registered through AddNodeDialog use "Infrared Camera".
        private static bool IsCamera(SensorItemViewModel s)
            => s.SensorType?.Contains("Camera", StringComparison.OrdinalIgnoreCase) == true;

        private bool HasCamera => Sensors.Any(IsCamera);
        private int NonCameraSensorCount => Sensors.Count(s => !IsCamera(s));

        // Instrument-tag chips for the list (CAM / SEN x n). Tags match the ID vocabulary
        // operators already see elsewhere (CAM-004, SEN-002), so no pictograms are needed.
        public Visibility CameraChipVisibility => HasCamera ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SensorChipVisibility => NonCameraSensorCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility UnconfiguredChipVisibility => Sensors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public string SensorChipText => $"SEN × {NonCameraSensorCount}";

        public string DeviceType
        {
            get
            {
                if (Sensors.Count == 0) return "Chưa cấu hình";
                int sensorCnt = NonCameraSensorCount;
                return (HasCamera, sensorCnt) switch
                {
                    (true, > 0) => $"Camera + {sensorCnt} cảm biến",
                    (true, 0)   => "Camera",
                    _           => $"{sensorCnt} cảm biến"
                };
            }
        }

        public string LastTelemetry =>
            Sensors.FirstOrDefault()?.LastUpdateText ?? "Chưa cập nhật";
    }

    /// <summary>ViewModel cho một yêu cầu gia nhập chờ phê duyệt.</summary>
    public partial class JoinRequestItemViewModel : ObservableObject
    {
        public int    Id              { get; set; }
        public string MacAddress      { get; set; } = string.Empty;
        public uint   HardwareId      { get; set; }
        public string FirmwareVersion { get; set; } = string.Empty;
        public string RequestedAt     { get; set; } = string.Empty;

        [ObservableProperty]
        private bool _isProcessing;

        partial void OnIsProcessingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsNotProcessing));
        }

        [ObservableProperty]
        private string _statusText = "Chờ phê duyệt";

        [ObservableProperty]
        private string _nodeByteIdInput = "1";

        internal DevicesViewModel? ViewModel { get; set; }

        public string HardwareIdHex => $"0x{HardwareId:X8}";
        public string DeviceName => $"NODE-{(HardwareId & 0xFFF):X3}";
        public string DeviceType => "Thiết bị cảm biến";
        public string FirmwareDisplay => string.IsNullOrWhiteSpace(FirmwareVersion) ? "-" : $"v{FirmwareVersion}";
        public string IpAddressDisplay => "-";
        public string MacAddressDisplay => MacAddress;
        public bool IsNotProcessing => !IsProcessing;

        [RelayCommand]
        private async Task Approve()
        {
            if (ViewModel == null || IsProcessing) return;
            if (!byte.TryParse(NodeByteIdInput, out byte nodeByteId)) nodeByteId = 1;
            IsProcessing = true;
            StatusText   = "Đang xử lý...";

            bool ok = await ViewModel.ApproveJoinAsync(Id, nodeByteId);
            StatusText   = ok ? "Đã chấp nhận" : "Lỗi";
            IsProcessing = false;

            if (ok) ViewModel.RemovePendingJoin(Id);
        }

        [RelayCommand]
        private async Task Reject()
        {
            if (ViewModel == null || IsProcessing) return;
            IsProcessing = true;
            StatusText   = "Đang xử lý...";

            bool ok = await ViewModel.RejectJoinAsync(Id);
            StatusText   = ok ? "Đã từ chối" : "Lỗi";
            IsProcessing = false;

            if (ok) ViewModel.RemovePendingJoin(Id);
        }
    }

    public partial class SensorItemViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _sensorId = string.Empty;

        [ObservableProperty]
        private string _sensorName = string.Empty;

        [ObservableProperty]
        private string _sensorType = string.Empty;

        public string SensorTypeDisplay => SensorType switch
        {
            "Radar"         => "RADAR",
            "Infrared"      => "HỒNG NGOẠI",
            "Temperature"   => "NHIỆT ĐỘ",
            "Humidity"      => "ĐỘ ẨM",
            "Light"         => "ÁNH SÁNG",
            "Accelerometer" => "GIA TỐC",
            "Camera"        => "CAMERA",
            _               => SensorType.ToUpper()
        };

        [ObservableProperty]
        private string _currentValue = string.Empty;

        [ObservableProperty]
        private string _unit = string.Empty;

        [ObservableProperty]
        private string _lastUpdateText = string.Empty;

        [ObservableProperty]
        private DeviceStatus _sensorStatus;

        [ObservableProperty]
        private string _typeIcon = string.Empty;

        [ObservableProperty]
        private string _lineName = string.Empty;

        [ObservableProperty]
        private string _nodeName = string.Empty;

        [ObservableProperty]
        private string _location = string.Empty;

        public SolidColorBrush SensorStatusColor
        {
            get => SensorStatus switch
            {
                DeviceStatus.Online => new SolidColorBrush(Color.FromArgb(255, 34, 197, 94)),
                DeviceStatus.Offline => new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
                DeviceStatus.Fault => new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)),
                DeviceStatus.Disabled => new SolidColorBrush(Color.FromArgb(255, 100, 116, 139)),
                _ => new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
            };
        }

        [RelayCommand]
        private async Task OpenSensorDetail()
        {
            try
            {
                // Create and show the Sensor Detail Dialog
                var dialog = new Station.Dialogs.SensorDetailDialog(this);

                // Set XamlRoot from App's main window
                if (Microsoft.UI.Xaml.Application.Current is App app && app.m_window is MainWindow mainWindow)
                {
                    dialog.XamlRoot = mainWindow.Content.XamlRoot;
                }

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening sensor detail dialog: {ex.Message}");
            }
        }
    }
}
