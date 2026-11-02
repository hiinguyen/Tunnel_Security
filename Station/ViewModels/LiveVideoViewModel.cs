using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Station.Models;
using Station.Services;

namespace Station.ViewModels
{
    public partial class LiveVideoViewModel : ObservableObject
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Timer _blinkTimer;
        private bool _blinkState = true;
        private readonly IDataService _mockData = DataServiceLocator.Current;

        // Current layout
        [ObservableProperty]
        private CameraGridLayout _currentLayout = CameraGridLayout.TwoByTwo;

        [ObservableProperty]
        private int _gridColumns = 2;

        [ObservableProperty]
        private int _gridRows = 2;

        // Camera streams
        public ObservableCollection<CameraStreamViewModel> CameraStreams { get; } = new();

        // Fixed display slots (count = GridColumns * GridRows). Cameras are assigned
        // to a slot by dragging them from the sidebar list; a slot with no camera
        // renders as an empty drop target.
        public ObservableCollection<CameraSlotViewModel> Slots { get; } = new();

        private bool _slotsInitialized = false;

        [ObservableProperty]
        private bool _hasEmptySlots = true;

        // Raised after Slots is cleared and rebuilt (layout change) so the view can
        // re-subscribe to the new slot instances and reposition their grid containers.
        public event Action? SlotsRebuilt;

        // Statistics
        [ObservableProperty]
        private int _activeCameras = 0;

        [ObservableProperty]
        private int _totalCameras = 0;

        [ObservableProperty]
        private string _selectedLayoutText = "2×2 (4 cameras)";

        // Alert state
        [ObservableProperty]
        private bool _hasActiveAlerts = false;

        [ObservableProperty]
        private int _activeAlertCount = 0;

        [ObservableProperty]
        private bool _alertBannerHighlight = true;

        // Event for requesting dialog display (raised to View)
        public event Action<CameraStreamViewModel>? AlertDialogRequested;

        public LiveVideoViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            LoadCameraStreams();
            ChangeLayout(CameraGridLayout.TwoByTwo);

            // Blink timer: 600ms toggle for alert border animation
            _blinkTimer = new Timer(600);
            _blinkTimer.Elapsed += OnBlinkTick;
            _blinkTimer.AutoReset = true;
            _blinkTimer.Start();

            _mockData.AlertGenerated += OnMockAlertGenerated;
            // With DATA_SOURCE=api, the camera list comes from BackendV2 asynchronously —
            // the initial LoadCameraStreams() above runs before that fetch completes, so we
            // must reload once TopologyLoaded fires (same pattern as DevicesViewModel).
            _mockData.TopologyLoaded += OnTopologyLoaded;
            _mockData.Start();
        }

        private void OnTopologyLoaded(object? sender, EventArgs e)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                LoadCameraStreams();
                ChangeLayout((int)CurrentLayout);
            });
        }

        private void OnMockAlertGenerated(object? sender, AlertGeneratedEventArgs e)
        {
            if (e.TriggeredByCameraId == null) return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                var cam = CameraStreams.FirstOrDefault(c => c.CameraId == e.TriggeredByCameraId);
                if (cam == null || !cam.IsOnline) return;

                var simCam = _mockData.Cameras.FirstOrDefault(c => c.CameraId == e.TriggeredByCameraId);
                string location = simCam?.Location ?? cam.CameraName;

                cam.TriggerAlert(e.Alert.Title, e.Alert.Description, e.Alert.Severity, location);
                RefreshAlertStats();
            });
        }

        private void OnBlinkTick(object? sender, ElapsedEventArgs e)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!HasActiveAlerts) return;
                _blinkState = !_blinkState;
                AlertBannerHighlight = _blinkState;
                foreach (var cam in CameraStreams.Where(c => c.HasAlert))
                    cam.AlertBorderOpacity = _blinkState ? 1.0 : 0.15;
            });
        }

        private void RefreshAlertStats()
        {
            ActiveAlertCount = CameraStreams.Count(c => c.HasAlert);
            HasActiveAlerts = ActiveAlertCount > 0;
        }

        private void LoadCameraStreams()
        {
            CameraStreams.Clear();

            var apiBaseUrl = Environment.GetEnvironmentVariable("BACKEND_BASE_URL") ?? "http://localhost:5280";
            int camIdx = 0;
            foreach (var simCam in _mockData.Cameras)
            {
                int idx = ++camIdx;
                CameraStreams.Add(new CameraStreamViewModel
                {
                    CameraId = simCam.CameraId,
                    CameraName = string.IsNullOrWhiteSpace(simCam.CameraName)
                        ? simCam.NodeName
                        : simCam.CameraName,
                    StreamUrl = simCam.StreamUrl ?? $"{apiBaseUrl}/api/cameras/{simCam.CameraId}/stream",
                    Resolution = "320×240",
                    IrStatus = "ON",
                    HdrStatus = "AUTO",
                    IsOnline = simCam.IsOnline,
                    IsRecording = simCam.IsOnline && idx <= 10,
                    Fps = 30,
                    Bitrate = 2.5
                });
            }

            TotalCameras = CameraStreams.Count;
            UpdateActiveCameras();
        }

        public void UpdateActiveCameras()
        {
            ActiveCameras = CameraStreams.Count(c => c.IsOnline);
        }

        [RelayCommand]
        private void ShowAlertVideo(CameraStreamViewModel? camera)
        {
            if (camera == null || !camera.HasAlert) return;
            AlertDialogRequested?.Invoke(camera);
        }

        [RelayCommand]
        private void ShowMostCriticalAlert()
        {
            var critical = CameraStreams
                .Where(c => c.HasAlert)
                .OrderByDescending(c => (int)c.AlertSeverityLevel)
                .FirstOrDefault();
            if (critical != null)
                AlertDialogRequested?.Invoke(critical);
        }

        [RelayCommand]
        private void ChangeLayout(object? parameter)
        {
            int count = 4;
            if (parameter is int c) count = c;
            else if (parameter is string s && int.TryParse(s, out int parsed)) count = parsed;

            switch (count)
            {
                case 1:
                    CurrentLayout = CameraGridLayout.Single;
                    GridColumns = 1; GridRows = 1;
                    SelectedLayoutText = "1×1 (1 camera)";
                    break;
                case 4:
                    CurrentLayout = CameraGridLayout.TwoByTwo;
                    GridColumns = 2; GridRows = 2;
                    SelectedLayoutText = "2×2 (4 cameras)";
                    break;
                case 9:
                    CurrentLayout = CameraGridLayout.ThreeByThree;
                    GridColumns = 3; GridRows = 3;
                    SelectedLayoutText = "3×3 (9 cameras)";
                    break;
                case 16:
                    CurrentLayout = CameraGridLayout.FourByFour;
                    GridColumns = 4; GridRows = 4;
                    SelectedLayoutText = "4×4 (16 cameras)";
                    break;
                default:
                    CurrentLayout = CameraGridLayout.TwoByTwo;
                    GridColumns = 2; GridRows = 2;
                    SelectedLayoutText = "2×2 (4 cameras)";
                    break;
            }

            RebuildSlots(count);
        }

        private void RebuildSlots(int count)
        {
            var previousAssignments = Slots.Select(s => s.AssignedCamera).ToList();
            Slots.Clear();
            for (int i = 0; i < count; i++)
            {
                // Span state doesn't carry across a layout change — every cell starts
                // as an independent 1x1 slot in its row-major grid position.
                var slot = new CameraSlotViewModel
                {
                    ChannelIndex = i + 1,
                    Row = i / GridColumns,
                    Column = i % GridColumns
                };
                if (i < previousAssignments.Count)
                    slot.AssignedCamera = previousAssignments[i];
                Slots.Add(slot);
            }

            if (!_slotsInitialized)
            {
                _slotsInitialized = true;
                AutoFillEmptySlots();
            }

            RefreshHasEmptySlots();
            RefreshResizeAffordances();
            UpdateActiveCameras();
            SlotsRebuilt?.Invoke();
        }

        private void AutoFillEmptySlots()
        {
            var unassigned = CameraStreams.Where(c => !IsCameraAssigned(c)).ToList();
            foreach (var slot in Slots)
            {
                if (slot.IsHiddenBySpan || slot.AssignedCamera != null) continue;
                var next = unassigned.FirstOrDefault();
                if (next == null) break;
                slot.AssignedCamera = next;
                unassigned.Remove(next);
            }
        }

        private bool IsCameraAssigned(CameraStreamViewModel camera) =>
            Slots.Any(s => s.AssignedCamera == camera);

        private void RefreshHasEmptySlots()
        {
            HasEmptySlots = Slots.Any(s => !s.IsHiddenBySpan && s.AssignedCamera == null);
        }

        private CameraSlotViewModel? SlotAt(int row, int col) =>
            Slots.FirstOrDefault(s => s.Row == row && s.Column == col);

        private static bool IsPlainSlot(CameraSlotViewModel? slot) =>
            slot != null && !slot.IsHiddenBySpan && slot.RowSpan == 1 && slot.ColumnSpan == 1;

        public bool CanExpandRight(CameraSlotViewModel slot)
        {
            int newCol = slot.Column + slot.ColumnSpan;
            if (newCol >= GridColumns) return false;
            for (int r = slot.Row; r < slot.Row + slot.RowSpan; r++)
                if (!IsPlainSlot(SlotAt(r, newCol))) return false;
            return true;
        }

        public bool CanExpandDown(CameraSlotViewModel slot)
        {
            int newRow = slot.Row + slot.RowSpan;
            if (newRow >= GridRows) return false;
            for (int c = slot.Column; c < slot.Column + slot.ColumnSpan; c++)
                if (!IsPlainSlot(SlotAt(newRow, c))) return false;
            return true;
        }

        /// Enumerates the slot(s) that would be absorbed if <paramref name="slot"/> grew
        /// one cell further right (isRight) or down (!isRight) — used both to actually
        /// perform the expand and, from the view, to preview which cell(s) a resize drag
        /// is about to absorb before the drag is released.
        public IEnumerable<CameraSlotViewModel> GetExpandNeighbors(CameraSlotViewModel slot, bool isRight)
        {
            if (isRight)
            {
                int newCol = slot.Column + slot.ColumnSpan;
                for (int r = slot.Row; r < slot.Row + slot.RowSpan; r++)
                {
                    var neighbor = SlotAt(r, newCol);
                    if (neighbor != null) yield return neighbor;
                }
            }
            else
            {
                int newRow = slot.Row + slot.RowSpan;
                for (int c = slot.Column; c < slot.Column + slot.ColumnSpan; c++)
                {
                    var neighbor = SlotAt(newRow, c);
                    if (neighbor != null) yield return neighbor;
                }
            }
        }

        /// Grows a slot to cover the column immediately to its right. The camera
        /// occupying that column (if any) is unassigned — "cameras that get occupied
        /// by the span automatically hide".
        public void ExpandColumn(CameraSlotViewModel slot)
        {
            if (!CanExpandRight(slot)) return;
            foreach (var neighbor in GetExpandNeighbors(slot, isRight: true))
            {
                neighbor.IsHiddenBySpan = true;
                neighbor.AssignedCamera = null;
            }
            slot.ColumnSpan++;
            RefreshHasEmptySlots();
            RefreshResizeAffordances();
            UpdateActiveCameras();
        }

        public void CollapseColumn(CameraSlotViewModel slot)
        {
            if (slot.ColumnSpan <= 1) return;
            int removedCol = slot.Column + slot.ColumnSpan - 1;
            for (int r = slot.Row; r < slot.Row + slot.RowSpan; r++)
            {
                var neighbor = SlotAt(r, removedCol);
                if (neighbor != null) neighbor.IsHiddenBySpan = false;
            }
            slot.ColumnSpan--;
            RefreshHasEmptySlots();
            RefreshResizeAffordances();
            UpdateActiveCameras();
        }

        /// Grows a slot to cover the row immediately below it, hiding whatever camera
        /// occupied that row.
        public void ExpandRow(CameraSlotViewModel slot)
        {
            if (!CanExpandDown(slot)) return;
            foreach (var neighbor in GetExpandNeighbors(slot, isRight: false))
            {
                neighbor.IsHiddenBySpan = true;
                neighbor.AssignedCamera = null;
            }
            slot.RowSpan++;
            RefreshHasEmptySlots();
            RefreshResizeAffordances();
            UpdateActiveCameras();
        }

        public void CollapseRow(CameraSlotViewModel slot)
        {
            if (slot.RowSpan <= 1) return;
            int removedRow = slot.Row + slot.RowSpan - 1;
            for (int c = slot.Column; c < slot.Column + slot.ColumnSpan; c++)
            {
                var neighbor = SlotAt(removedRow, c);
                if (neighbor != null) neighbor.IsHiddenBySpan = false;
            }
            slot.RowSpan--;
            RefreshHasEmptySlots();
            RefreshResizeAffordances();
            UpdateActiveCameras();
        }

        /// Updates each slot's resize-grip visibility: a grip only shows when the
        /// slot can actually grow in that direction, or is already expanded (so it
        /// can be dragged back).
        private void RefreshResizeAffordances()
        {
            foreach (var slot in Slots)
            {
                slot.ShowRightGrip = !slot.IsHiddenBySpan && slot.AssignedCamera != null
                    && (slot.ColumnSpan > 1 || CanExpandRight(slot));
                slot.ShowBottomGrip = !slot.IsHiddenBySpan && slot.AssignedCamera != null
                    && (slot.RowSpan > 1 || CanExpandDown(slot));
            }
        }

        /// Called from the view when a camera is dropped onto a slot.
        public void AssignCameraToSlot(int slotIndex, string cameraId)
        {
            if (slotIndex < 0 || slotIndex >= Slots.Count) return;

            var slot = Slots[slotIndex];
            if (slot.IsHiddenBySpan) return;

            var camera = CameraStreams.FirstOrDefault(c => c.CameraId == cameraId);
            if (camera == null) return;

            // A camera can only occupy one slot at a time — moving it clears the old slot.
            var existingSlot = Slots.FirstOrDefault(s => s.AssignedCamera == camera);
            if (existingSlot != null)
                existingSlot.AssignedCamera = null;

            slot.AssignedCamera = camera;
            RefreshHasEmptySlots();
            RefreshResizeAffordances();
            UpdateActiveCameras();
        }

        [RelayCommand]
        private void ClearSlot(CameraSlotViewModel? slot)
        {
            if (slot == null) return;
            slot.AssignedCamera = null;
            RefreshHasEmptySlots();
            RefreshResizeAffordances();
            UpdateActiveCameras();
        }

        // Keyboard/non-drag equivalent of dragging a sidebar camera onto a slot:
        // drops it into the first free slot. Button that invokes this is disabled
        // via HasEmptySlots when the grid is full, so a no-op here just means the
        // user re-clicked before the UI caught up.
        [RelayCommand]
        private void AssignToFirstEmptySlot(CameraStreamViewModel? camera)
        {
            if (camera == null || IsCameraAssigned(camera)) return;

            var slot = Slots.FirstOrDefault(s => !s.IsHiddenBySpan && s.AssignedCamera == null);
            if (slot == null) return;

            AssignCameraToSlot(Slots.IndexOf(slot), camera.CameraId);
        }

        [RelayCommand]
        private void RefreshStreams()
        {
            LoadCameraStreams();
        }

        [RelayCommand]
        private void TakeSnapshot(CameraStreamViewModel? camera)
        {
            if (camera == null) return;
            System.Diagnostics.Debug.WriteLine($"Snapshot taken for {camera.CameraName}");
        }

        [RelayCommand]
        private void ToggleRecording(CameraStreamViewModel? camera)
        {
            if (camera == null) return;
            camera.IsRecording = !camera.IsRecording;
        }

        [RelayCommand]
        private void ToggleStream(CameraStreamViewModel? camera)
        {
            if (camera == null) return;
            camera.IsStreamEnabled = !camera.IsStreamEnabled;
            UpdateActiveCameras();
        }

        [RelayCommand]
        private void ShowCameraSettings(CameraStreamViewModel? camera) { }

        [ObservableProperty]
        private CameraStreamViewModel? _focusedCamera;

        [ObservableProperty]
        private bool _isCameraFocused = false;

        [RelayCommand]
        private void FocusCamera(CameraStreamViewModel? camera)
        {
            FocusedCamera = camera;
            IsCameraFocused = camera != null;
        }

        [RelayCommand]
        private void ExitFocus()
        {
            FocusedCamera = null;
            IsCameraFocused = false;
        }
    }

    public partial class CameraStreamViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _cameraId = string.Empty;

        [ObservableProperty]
        private string _cameraName = string.Empty;

        [ObservableProperty]
        private string _streamUrl = string.Empty;

        [ObservableProperty]
        private string _resolution = "1280×720";

        [ObservableProperty]
        private string _irStatus = "AUTO";

        [ObservableProperty]
        private string _hdrStatus = "AUTO";

        [ObservableProperty]
        private bool _isOnline;

        [ObservableProperty]
        private bool _isRecording;

        [ObservableProperty]
        private int _fps;

        [ObservableProperty]
        private double _bitrate;

        // Stream enabled/disabled toggle — when false, no frames are requested from backend.
        [ObservableProperty]
        private bool _isStreamEnabled = true;

        partial void OnIsStreamEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(StreamToggleIcon));
            OnPropertyChanged(nameof(StreamToggleTooltip));
            OnPropertyChanged(nameof(StreamToggleColor));
        }

        public string StreamToggleIcon => _isStreamEnabled ? "" : "";
        public string StreamToggleTooltip => _isStreamEnabled ? "Tạm dừng nhận stream" : "Bật nhận stream";
        public SolidColorBrush StreamToggleColor => _isStreamEnabled
            ? new SolidColorBrush(Colors.Gray)
            : ResolveBrush("LiveVideoPausedIndicatorBrush", Windows.UI.Color.FromArgb(255, 251, 191, 36));

        // Path to a local video file — set by FileOpenPicker in code-behind.
        // When non-null, CameraVideoControl plays the file instead of the MJPEG stream.
        [ObservableProperty]
        private string? _localVideoPath;

        // Alert state
        [ObservableProperty]
        private bool _hasAlert = false;

        [ObservableProperty]
        private double _alertBorderOpacity = 1.0;

        [ObservableProperty]
        private string _alertTitle = string.Empty;

        [ObservableProperty]
        private string _alertDescription = string.Empty;

        [ObservableProperty]
        private AlertSeverity _alertSeverityLevel = AlertSeverity.Low;

        [ObservableProperty]
        private string _alertLocation = string.Empty;

        [ObservableProperty]
        private DateTimeOffset _alertTime = DateTimeOffset.Now;

        // Computed display helpers

        // Resolves a theme-dictionary brush by key so status/severity colors stay in
        // sync with Colors.xaml (incl. Light/Dark variants) instead of duplicating
        // hex values here — same pattern as ColorConverters.cs / DevicesPage.xaml.cs.
        private static SolidColorBrush ResolveBrush(string key, Windows.UI.Color fallback) =>
            Station.Services.ThemeBrushes.TryGet(key, out var resource) && resource is SolidColorBrush brush
                ? brush
                : new SolidColorBrush(fallback);

        public string StatusText => _isOnline ? "Online" : "Offline";

        public SolidColorBrush StatusColor => ResolveBrush(
            _isOnline ? "MonitoringNodeNormalBrush" : "MonitoringNodeOfflineBrush",
            _isOnline ? Windows.UI.Color.FromArgb(255, 63, 207, 142) : Windows.UI.Color.FromArgb(255, 123, 126, 133));

        // Fill is transparent while offline so the status dot renders as a hollow
        // ring rather than a solid one — a colorblind-safe shape cue alongside the
        // hue, per PRODUCT.md's "not color-alone" accessibility bar.
        public SolidColorBrush StatusFillBrush => _isOnline
            ? StatusColor
            : new SolidColorBrush(Colors.Transparent);

        partial void OnIsOnlineChanged(bool value)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusFillBrush));
        }

        public string RecordingIcon  => _isRecording ? "" : "";

        public SolidColorBrush RecordingColor => _isRecording
            ? new SolidColorBrush(Colors.Red)
            : new SolidColorBrush(Colors.Gray);

        public string BitrateDisplay => $"{_bitrate:F1} Mbps";

        /// Short filename shown in the footer chip when a local video is loaded.
        public string LocalVideoFileName =>
            string.IsNullOrEmpty(_localVideoPath) ? string.Empty : Path.GetFileName(_localVideoPath);

        /// True when a local video file is loaded — used to toggle the footer chip visibility.
        public bool HasLocalVideo => !string.IsNullOrEmpty(_localVideoPath);

        partial void OnLocalVideoPathChanged(string? value)
        {
            OnPropertyChanged(nameof(LocalVideoFileName));
            OnPropertyChanged(nameof(HasLocalVideo));
        }

        public string AlertSeverityText => _alertSeverityLevel switch
        {
            AlertSeverity.Critical => "KHẨN CẤP",
            AlertSeverity.High     => "NGUY HIỂM",
            AlertSeverity.Medium   => "TRUNG BÌNH",
            _                      => "THẤP"
        };

        // Routed through Colors.xaml's SeverityXBrush tokens (same brushes every other
        // severity-graded surface in the app uses) instead of duplicating raw ARGB here —
        // keeps this card's border/dot/icon in lockstep with the app-wide severity grammar,
        // including its Dark-theme hue adjustments.
        public SolidColorBrush AlertSeverityBrush => _alertSeverityLevel switch
        {
            AlertSeverity.Critical => ResolveBrush("SeverityCriticalBrush", Windows.UI.Color.FromArgb(255, 239, 68, 68)),
            AlertSeverity.High     => ResolveBrush("SeverityHighBrush",     Windows.UI.Color.FromArgb(255, 249, 115, 22)),
            AlertSeverity.Medium   => ResolveBrush("SeverityMediumBrush",   Windows.UI.Color.FromArgb(255, 234, 179, 8)),
            _                      => ResolveBrush("SeverityLowBrush",      Windows.UI.Color.FromArgb(255, 34, 197, 94))
        };

        // Color (not Brush) form for binding into SolidColorBrush.Color, e.g. the alert
        // card's pulsing border where Opacity is animated independently of the hue.
        public Windows.UI.Color AlertSeverityColor => AlertSeverityBrush.Color;

        public string AlertTimeDisplay => _alertTime.ToString("HH:mm:ss dd/MM/yyyy");

        public void TriggerAlert(string title, string description, AlertSeverity severity, string location)
        {
            AlertTitle = title;
            AlertDescription = description;
            AlertSeverityLevel = severity;
            AlertLocation = location;
            AlertTime = DateTimeOffset.Now;
            AlertBorderOpacity = 1.0;
            HasAlert = true;

            OnPropertyChanged(nameof(AlertSeverityText));
            OnPropertyChanged(nameof(AlertSeverityBrush));
            OnPropertyChanged(nameof(AlertSeverityColor));
            OnPropertyChanged(nameof(AlertTimeDisplay));
        }

        public void DismissAlert()
        {
            HasAlert = false;
            AlertBorderOpacity = 0.0;
            AlertTitle = string.Empty;
            AlertDescription = string.Empty;
        }

        [RelayCommand]
        private void SetResolution(string? res)
        {
            if (!string.IsNullOrEmpty(res)) Resolution = res;
        }
    }

    /// One cell in the camera display grid — either empty (a drop target) or holding an
    /// assigned camera. Row/Column are its fixed row-major position; RowSpan/ColumnSpan
    /// grow to 2 when the user drags a resize grip, at which point IsHiddenBySpan is set
    /// on the cell(s) it now covers so they stop rendering as independent drop targets.
    public partial class CameraSlotViewModel : ObservableObject
    {
        [ObservableProperty]
        private int _channelIndex;

        [ObservableProperty]
        private CameraStreamViewModel? _assignedCamera;

        [ObservableProperty]
        private int _row;

        [ObservableProperty]
        private int _column;

        [ObservableProperty]
        private int _rowSpan = 1;

        [ObservableProperty]
        private int _columnSpan = 1;

        [ObservableProperty]
        private bool _isHiddenBySpan;

        [ObservableProperty]
        private bool _showRightGrip;

        [ObservableProperty]
        private bool _showBottomGrip;

        // True while a dragged camera is hovering over this (empty) slot — drives the
        // animated highlight on the dashed drop-target border.
        [ObservableProperty]
        private bool _isDragHighlighted;

        // True while a resize-grip drag on a neighboring slot has crossed the expand
        // threshold and would absorb this slot on release — dims the card live, tracking
        // the pointer, so it un-dims immediately if the drag retreats before release.
        [ObservableProperty]
        private bool _isResizePreviewDimmed;

        public bool IsEmpty => _assignedCamera == null;

        public string SlotLabel => $"Trống (Kênh {_channelIndex:00})";

        public double DragHighlightOpacity => _isDragHighlighted ? 0.4 : 0;

        public double ResizePreviewOpacity => _isResizePreviewDimmed ? 0.35 : 1.0;

        partial void OnAssignedCameraChanged(CameraStreamViewModel? value)
        {
            OnPropertyChanged(nameof(IsEmpty));
        }

        partial void OnIsDragHighlightedChanged(bool value) => OnPropertyChanged(nameof(DragHighlightOpacity));

        partial void OnIsResizePreviewDimmedChanged(bool value) => OnPropertyChanged(nameof(ResizePreviewOpacity));
    }
}
