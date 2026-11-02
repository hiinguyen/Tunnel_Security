using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WinUI;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Station.Models;
using Station.Services;

namespace Station.Views;

public sealed partial class SensorChartsPage : Page
{
    private readonly IDataService _dataService = DataServiceLocator.Current;
    private readonly Dictionary<string, SensorChartState> _sensorStates = new();
    private readonly Dictionary<string, ObservableCollection<double>> _chartHistories = new();

    // Fixed display slots (count = columns * RowsPerLayout), positioned in a real Grid by
    // Row/Column/RowSpan/ColumnSpan — same model as LiveVideoPage's CameraSlotViewModel. A
    // sensor is assigned to a slot by dragging it from the sidebar list (or an already-
    // placed card) onto it; a slot with no assignment renders as an empty dashed drop
    // target. Dragging a card's right/bottom edge grip grows its Column/RowSpan by exactly
    // one grid cell — a ratio of the available space, never a raw pixel amount, so a card
    // can only ever occupy whole cells and the layout can't overflow past the viewport the
    // way free-form pixel resizing could. Growing a span into a neighboring cell hides that
    // cell (IsHiddenBySpan) and unassigns whatever sensor it held.
    private sealed class ChartSlot
    {
        public string? SensorId { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int RowSpan { get; set; } = 1;
        public int ColumnSpan { get; set; } = 1;
        public bool IsHiddenBySpan { get; set; }
    }

    // UIElement.ProtectedCursor is protected, so a plain element can't change its own
    // hover cursor — this thin subclass exposes it for the corner resize grips.
    // (Border is sealed in WinUI3, so this derives from Grid instead.)
    private sealed class CursorGrid : Grid
    {
        public InputCursor? HoverCursor
        {
            get => ProtectedCursor;
            set => ProtectedCursor = value;
        }
    }

    private readonly List<ChartSlot> _slots = new();

    // Maps each visible (non-hidden) slot to whatever FrameworkElement currently
    // represents it on ChartsHost — populated by BuildCharts() and used by ExpandCard()/
    // CollapseExpandedCard() to find the card to re-parent into the fullscreen overlay.
    private readonly Dictionary<ChartSlot, FrameworkElement> _slotElements = new();

    private bool _slotsInitialized;

    // The slot currently shown in the fullscreen overlay (mirrors LiveVideoPage's
    // FocusedCamera/IsCameraFocused pair) — null when no card is expanded. Kept as a
    // slot reference rather than a bool+id pair so ReflowCanvas() can cheaply exclude
    // it from the packing pass while its card lives outside ChartsHost.
    private ChartSlot? _expandedSlot;

    // The FLIP-transform starting values captured by ComputeFullscreenFlipTransform() at the
    // moment a card is expanded — how much smaller/offset its original grid-cell bounds were
    // compared to the fullscreen target rect. Reused as-is when collapsing (animating back
    // down to these same values) since layout can't change while a card is expanded (resize
    // grips no-op on the expanded slot, and a rebuild force-collapses instantly beforehand).
    private double _fsScaleX = 1, _fsScaleY = 1, _fsTranslateX, _fsTranslateY;

    private record TabItem(Border Btn, TextBlock Lbl);
    private readonly Dictionary<string, TabItem> _layoutItems = new();
    private readonly Dictionary<string, TabItem> _histItems = new();

    // Sidebar sensor list is grouped by node — a node's sensor rows only render
    // (and become draggable) while its group header is expanded.
    private readonly HashSet<string> _expandedNodes = new();

    // Resolves a brush/color from Colors.xaml by key so this page's palette stays in
    // sync with the shared design tokens instead of duplicating raw ARGB values here —
    // same pattern as LiveVideoViewModel.ResolveBrush().
    private static Color ResolveColor(string key, Color fallback) =>
        Station.Services.ThemeBrushes.TryGet(key, out var resource) && resource is SolidColorBrush brush
            ? brush.Color
            : fallback;

    private static SolidColorBrush ResolveBrush(string key, Color fallback) => new(ResolveColor(key, fallback));

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static readonly SolidColorBrush _rowHoverBrush = ResolveBrush("DkRowHoverOverlayBrush", Color.FromArgb(18, 255, 255, 255));
    private static readonly SolidColorBrush _rowIdleBrush  = new(Colors.Transparent);

    private DispatcherTimer? _renderTimer;
    private const int RenderIntervalMs = 200;

    // Fixed row count for the charts Grid — column count is the user-selectable layout
    // preset (_columns); total slots = _columns * RowsPerLayout, same idea as
    // LiveVideoPage's GridRows/GridColumns.
    private const int RowsPerLayout = 3;

    // Only the central fraction of a card's area can start a card-move drag — the
    // remaining border band is left for the corner resize grips and for interacting
    // with the header/footer/chart without accidentally picking the card up.
    private const double CardDragZoneFraction = 0.5;

    private int _columns = 2;
    private int _historyLength = 60;
    private int _totalUpdates;

    public SensorChartsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ─────────── Lifecycle ───────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _layoutItems["1"] = new TabItem(LayoutOpt1, LblLayout1);
        _layoutItems["2"] = new TabItem(LayoutOpt2, LblLayout2);
        _layoutItems["3"] = new TabItem(LayoutOpt3, LblLayout3);
        UpdateLayoutItemStates();

        _histItems["30"]  = new TabItem(HistOpt30,  HistLbl30);
        _histItems["60"]  = new TabItem(HistOpt60,  HistLbl60);
        _histItems["100"] = new TabItem(HistOpt100, HistLbl100);
        UpdateHistItemStates();

        _dataService.TopologyLoaded += OnTopologyLoaded;
        _dataService.SensorTick += OnSensorTick;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RenderIntervalMs) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();

        RebuildSlotCount();
        BuildCharts();
        RebuildSensorList();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _dataService.TopologyLoaded -= OnTopologyLoaded;
        _dataService.SensorTick -= OnSensorTick;

        _renderTimer?.Stop();
        _renderTimer = null;
    }

    private void OnTopologyLoaded(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_slotsInitialized && _dataService.Sensors.Count > 0)
            {
                _slotsInitialized = true;
                AutoFillEmptySlots();
            }
            BuildCharts();
            RebuildSensorList();
        });
    }

    private void OnSensorTick(object? sender, SensorTickEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            try { ProcessSensorTick(e); }
            catch (Exception ex) { Debug.WriteLine($"[SensorChartsPage] Tick error: {ex.Message}"); }
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { ProcessSensorTick(e); }
                catch (Exception ex) { Debug.WriteLine($"[SensorChartsPage] Tick error: {ex.Message}"); }
            });
        }
    }

    // ─────────── Render loop ───────────

    private void OnRenderTick(object? sender, object e)
    {
        foreach (var state in _sensorStates.Values)
        {
            state.ChartValues.Add(state.LastValue);
            if (state.ChartValues.Count > _historyLength)
                state.ChartValues.RemoveAt(0);
        }
    }

    // ─────────── Sensor tick ───────────

    private void ProcessSensorTick(SensorTickEventArgs e)
    {
        _totalUpdates++;
        TotalUpdatesText.Text = _totalUpdates.ToString("N0");

        if (!_sensorStates.TryGetValue(e.Sensor.SensorId, out var state)) return;

        state.LastValue = e.NewValue;
        state.ValueText.Text = $"{e.NewValue:F1}";

        var (dotColor, statusLabel) = e.Sensor.CurrentLevel switch
        {
            SensorAlertLevel.Critical => (ResolveColor("DkRedBrush",   Color.FromArgb(255, 255, 82,  82)),  "CRITICAL"),
            SensorAlertLevel.Warning  => (ResolveColor("DkYellowBrush", Color.FromArgb(255, 255, 209, 102)), "WARNING"),
            SensorAlertLevel.Offline  => (ResolveColor("DkGrayBrush",   Color.FromArgb(255, 123, 126, 133)), "OFFLINE"),
            _                         => (ResolveColor("DkGreenBrush",  Color.FromArgb(255, 63,  207, 142)), "NORMAL")
        };

        var brush = new SolidColorBrush(dotColor);
        state.StatusDot.Background = brush;
        state.StatusText.Text = statusLabel;
        state.StatusText.Foreground = brush;
    }

    // ─────────── Sidebar: layout (column count) selection ───────────

    private void LayoutOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string key && int.TryParse(key, out var columns))
        {
            _columns = columns;
            UpdateLayoutItemStates();
            RebuildSlotCount();
            BuildCharts();
            RebuildSensorList();
        }
    }

    // ─────────── Header: history length selection ───────────

    private void HistoryOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is string key && int.TryParse(key, out var length))
        {
            _historyLength = length;
            UpdateHistItemStates();

            foreach (var state in _sensorStates.Values)
            {
                while (state.ChartValues.Count > _historyLength)
                    state.ChartValues.RemoveAt(0);
            }
        }
    }

    // ─────────── Header: auto-fill empty slots ───────────

    private void AutoFill_Click(object sender, RoutedEventArgs e)
    {
        AutoFillEmptySlots();
        BuildCharts();
        RebuildSensorList();
    }

    // ─────────── Tab state styling ───────────

    private static void ApplyActiveState(TabItem item, bool active)
    {
        var activeBg     = ResolveColor("DkBlueBgBrush",     Color.FromArgb(255, 13,  29,  59));
        var activeBorder = ResolveColor("DkBlueLightBrush",  Color.FromArgb(255, 96,  165, 250));
        var activeText   = activeBorder;
        var inactiveText = ResolveColor("DkTextMutedBrush",  Color.FromArgb(255, 150, 160, 179));
        var transparent  = Colors.Transparent;

        item.Btn.Background  = new SolidColorBrush(active ? activeBg : transparent);
        item.Btn.BorderBrush = new SolidColorBrush(active ? activeBorder : transparent);
        item.Lbl.Foreground  = new SolidColorBrush(active ? activeText : inactiveText);
    }

    private void UpdateLayoutItemStates()
    {
        foreach (var (key, item) in _layoutItems)
            ApplyActiveState(item, key == _columns.ToString());
    }

    private void UpdateHistItemStates()
    {
        foreach (var (key, item) in _histItems)
            ApplyActiveState(item, key == _historyLength.ToString());
    }

    // ─────────── Slot management ───────────

    private void RebuildSlotCount()
    {
        var columns = Math.Max(1, _columns);
        var count = columns * RowsPerLayout;

        // Picking an explicit column preset means "give me a clean N-per-row grid" —
        // every slot's Row/Column/Span resets fresh to a plain 1x1 cell in the new grid
        // rather than carrying over spans from a previous preset, which could otherwise
        // land two spans on the same cell under the new column count. Sensor assignments
        // still carry over by position, same idea as before.
        var previousAssignments = _slots.Select(s => s.SensorId).ToList();
        _slots.Clear();
        for (var i = 0; i < count; i++)
        {
            var slot = new ChartSlot { Row = i / columns, Column = i % columns };
            if (i < previousAssignments.Count) slot.SensorId = previousAssignments[i];
            _slots.Add(slot);
        }
    }

    private void AutoFillEmptySlots()
    {
        var unassigned = _dataService.Sensors
            .Where(s => _slots.All(slot => slot.SensorId != s.SensorId))
            .OrderBy(s => s.LineId)
            .ThenBy(s => s.NodeId)
            .ToList();

        foreach (var slot in _slots)
        {
            if (slot.IsHiddenBySpan || slot.SensorId != null) continue;
            var next = unassigned.FirstOrDefault();
            if (next == null) break;
            slot.SensorId = next.SensorId;
            unassigned.Remove(next);
        }
    }

    /// Called when a sensor is dropped onto a slot (from the sidebar list or from
    /// another already-displayed card). A sensor can only occupy one slot at a time —
    /// moving it clears whatever slot it previously held.
    private void AssignSensorToSlot(int slotIndex, string sensorId)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Count) return;
        if (_slots[slotIndex].IsHiddenBySpan) return;
        if (_dataService.Sensors.All(s => s.SensorId != sensorId)) return;

        var existing = _slots.FirstOrDefault(s => s.SensorId == sensorId);
        if (existing != null) existing.SensorId = null;

        _slots[slotIndex].SensorId = sensorId;
        BuildCharts();
        RebuildSensorList();
    }

    private void UnassignSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Count) return;
        _slots[slotIndex].SensorId = null;
        BuildCharts();
        RebuildSensorList();
    }

    // ─────────── Grid layout: row/column spans (ratio-based, mirrors LiveVideoPage) ───────────

    /// Keeps ChartsHost's RowDefinitions/ColumnDefinitions in sync with RowsPerLayout and
    /// the current column preset. Every definition is left at its default Star size, so
    /// each cell always gets an equal share of the available space — a card spanning N
    /// cells always occupies exactly N/columns of the width, never a raw pixel amount.
    private void RebuildChartsGridDefinitions()
    {
        if (ChartsHost.RowDefinitions.Count != RowsPerLayout)
        {
            ChartsHost.RowDefinitions.Clear();
            for (var i = 0; i < RowsPerLayout; i++)
                ChartsHost.RowDefinitions.Add(new RowDefinition());
        }

        var columns = Math.Max(1, _columns);
        if (ChartsHost.ColumnDefinitions.Count != columns)
        {
            ChartsHost.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++)
                ChartsHost.ColumnDefinitions.Add(new ColumnDefinition());
        }
    }

    private ChartSlot? SlotAt(int row, int col) =>
        _slots.FirstOrDefault(s => s.Row == row && s.Column == col);

    private static bool IsPlainSlot(ChartSlot? slot) =>
        slot != null && !slot.IsHiddenBySpan && slot.RowSpan == 1 && slot.ColumnSpan == 1;

    private bool CanExpandRight(ChartSlot slot)
    {
        var newCol = slot.Column + slot.ColumnSpan;
        if (newCol >= Math.Max(1, _columns)) return false;
        for (var r = slot.Row; r < slot.Row + slot.RowSpan; r++)
            if (!IsPlainSlot(SlotAt(r, newCol))) return false;
        return true;
    }

    private bool CanExpandDown(ChartSlot slot)
    {
        var newRow = slot.Row + slot.RowSpan;
        if (newRow >= RowsPerLayout) return false;
        for (var c = slot.Column; c < slot.Column + slot.ColumnSpan; c++)
            if (!IsPlainSlot(SlotAt(newRow, c))) return false;
        return true;
    }

    /// The neighbor slot(s) that would be absorbed if `slot` grew one more cell in the given
    /// direction — shared by Expand*() (which actually absorbs them) and BuildEdgeGrip()'s
    /// live drag preview (which just dims them without committing anything yet).
    private IEnumerable<ChartSlot> NeighborsForExpand(ChartSlot slot, bool isRight)
    {
        if (isRight)
        {
            var newCol = slot.Column + slot.ColumnSpan;
            for (var r = slot.Row; r < slot.Row + slot.RowSpan; r++)
            {
                var neighbor = SlotAt(r, newCol);
                if (neighbor != null) yield return neighbor;
            }
        }
        else
        {
            var newRow = slot.Row + slot.RowSpan;
            for (var c = slot.Column; c < slot.Column + slot.ColumnSpan; c++)
            {
                var neighbor = SlotAt(newRow, c);
                if (neighbor != null) yield return neighbor;
            }
        }
    }

    /// Grows a slot to cover the column immediately to its right — by exactly one grid
    /// cell, so the row's total width stays exactly what it was (a ratio, never a raw
    /// pixel amount). Whatever sensor occupied that column is unassigned, same as
    /// LiveVideoPage's camera-grid span model.
    private void ExpandColumn(ChartSlot slot)
    {
        if (!CanExpandRight(slot)) return;
        foreach (var neighbor in NeighborsForExpand(slot, isRight: true))
        {
            neighbor.IsHiddenBySpan = true;
            neighbor.SensorId = null;
        }
        slot.ColumnSpan++;
        BuildCharts();
        RebuildSensorList();
    }

    private void CollapseColumn(ChartSlot slot)
    {
        if (slot.ColumnSpan <= 1) return;
        var removedCol = slot.Column + slot.ColumnSpan - 1;
        for (var r = slot.Row; r < slot.Row + slot.RowSpan; r++)
        {
            var neighbor = SlotAt(r, removedCol);
            if (neighbor != null) neighbor.IsHiddenBySpan = false;
        }
        slot.ColumnSpan--;
        BuildCharts();
        RebuildSensorList();
    }

    /// Grows a slot to cover the row immediately below it, hiding whatever sensor
    /// occupied that row.
    private void ExpandRow(ChartSlot slot)
    {
        if (!CanExpandDown(slot)) return;
        foreach (var neighbor in NeighborsForExpand(slot, isRight: false))
        {
            neighbor.IsHiddenBySpan = true;
            neighbor.SensorId = null;
        }
        slot.RowSpan++;
        BuildCharts();
        RebuildSensorList();
    }

    private void CollapseRow(ChartSlot slot)
    {
        if (slot.RowSpan <= 1) return;
        var removedRow = slot.Row + slot.RowSpan - 1;
        for (var c = slot.Column; c < slot.Column + slot.ColumnSpan; c++)
        {
            var neighbor = SlotAt(removedRow, c);
            if (neighbor != null) neighbor.IsHiddenBySpan = false;
        }
        slot.RowSpan--;
        BuildCharts();
        RebuildSensorList();
    }

    // ─────────── Sidebar: sensor list (drag source) ───────────

    private void RebuildSensorList()
    {
        SensorListPanel.Children.Clear();

        var groups = _dataService.Sensors
            .GroupBy(s => s.NodeId)
            .OrderBy(g => g.First().LineId)
            .ThenBy(g => g.Key);

        foreach (var group in groups)
        {
            var sensors = group
                .OrderBy(s => SensorLabel(SensorTypeString(s.Category)))
                .ToList();
            SensorListPanel.Children.Add(BuildNodeGroup(group.Key, sensors[0].NodeName, sensors));
        }
    }

    /// One collapsible node tab in the sidebar: a tappable header (node name + how many
    /// of its sensors are currently displayed) that expands to reveal that node's
    /// individual sensors as drag sources.
    private StackPanel BuildNodeGroup(string nodeId, string nodeName, List<SimulatedSensor> sensors)
    {
        var expanded = _expandedNodes.Contains(nodeId);
        var assignedCount = sensors.Count(s => _slots.Any(slot => slot.SensorId == s.SensorId));

        var header = new Grid
        {
            Padding    = new Thickness(18, 10, 14, 10),
            Background = _rowIdleBrush,
            IsTapEnabled = true
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chevron = new FontIcon
        {
            Glyph      = expanded ? "" : "",
            FontSize   = 9,
            Foreground = ResolveBrush("DkGrayBrush", Color.FromArgb(255, 123, 126, 133)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 0);

        var nameTb = new TextBlock
        {
            Text         = nodeName,
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI Variable"),
            FontWeight   = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground   = ResolveBrush("DkTextSubtleBrush", Color.FromArgb(255, 194, 198, 214)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameTb, 1);

        var countTb = new TextBlock
        {
            Text       = $"{assignedCount}/{sensors.Count}",
            FontSize   = 9,
            FontFamily = new FontFamily("Segoe UI Variable"),
            Foreground = ResolveBrush("DkTextFaintBrush", Color.FromArgb(255, 90, 97, 110)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countTb, 2);

        header.Children.Add(chevron);
        header.Children.Add(nameTb);
        header.Children.Add(countTb);

        header.Tapped += (_, _) =>
        {
            if (!_expandedNodes.Add(nodeId))
                _expandedNodes.Remove(nodeId);
            RebuildSensorList();
        };
        header.PointerEntered += (_, _) => header.Background = _rowHoverBrush;
        header.PointerExited  += (_, _) => header.Background = _rowIdleBrush;

        var group = new StackPanel();
        group.Children.Add(header);

        if (expanded)
        {
            var childStack = new StackPanel { Margin = new Thickness(14, 0, 0, 4) };
            foreach (var sensor in sensors)
                childStack.Children.Add(BuildSensorRow(sensor));
            group.Children.Add(childStack);
        }

        return group;
    }

    private Grid BuildSensorRow(SimulatedSensor sensor)
    {
        var type = SensorTypeString(sensor.Category);
        var accent = AccentColor(type);
        var isAssigned = _slots.Any(slot => slot.SensorId == sensor.SensorId);

        var row = new Grid
        {
            Padding    = new Thickness(14, 9, 14, 9),
            Background = _rowIdleBrush,
            Opacity    = isAssigned ? 0.4 : 1.0,
            CanDrag    = !isAssigned
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(dot, 0);

        var nameTb = new TextBlock
        {
            Text       = SensorLabel(type),
            FontSize   = 11,
            FontFamily = new FontFamily("Segoe UI Variable"),
            Foreground = ResolveBrush("DkTextSubtleBrush", Color.FromArgb(255, 194, 198, 214)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var typeTb = new TextBlock
        {
            Text       = sensor.SensorId,
            FontSize   = 9,
            Foreground = ResolveBrush("DkTextFaintBrush", Color.FromArgb(255, 90, 97, 110)),
            Margin     = new Thickness(0, 2, 0, 0)
        };
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        textStack.Children.Add(nameTb);
        textStack.Children.Add(typeTb);
        Grid.SetColumn(textStack, 1);

        var addIcon = new FontIcon { Glyph = "", FontSize = 11, Foreground = ResolveBrush("DkGrayBrush", Color.FromArgb(255, 123, 126, 133)) };
        var addBtn = new Button
        {
            Content         = addIcon,
            Background      = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Width           = 24,
            Height          = 24,
            Padding         = new Thickness(0),
            IsEnabled       = !isAssigned,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(addBtn, isAssigned ? "Đã hiển thị" : "Thêm vào ô trống");
        addBtn.Click += (_, _) =>
        {
            var idx = _slots.FindIndex(slot => slot.SensorId == null);
            if (idx < 0) return;
            AssignSensorToSlot(idx, sensor.SensorId);
        };
        Grid.SetColumn(addBtn, 2);

        row.Children.Add(dot);
        row.Children.Add(textStack);
        row.Children.Add(addBtn);

        if (!isAssigned)
        {
            row.DragStarting += (_, e) =>
            {
                e.Data.SetText(sensor.SensorId);
                e.Data.RequestedOperation = DataPackageOperation.Copy;
                row.Opacity = 0.4;
            };
            row.DropCompleted += (_, _) => row.Opacity = 1.0;
            row.PointerEntered += (_, _) => row.Background = _rowHoverBrush;
            row.PointerExited  += (_, _) => row.Background = _rowIdleBrush;
        }

        return row;
    }

    // ─────────── Chart building ───────────

    private void BuildCharts()
    {
        // A full rebuild is about to discard and recreate every card from scratch; if one
        // is currently detached into the fullscreen overlay, put it back first so it isn't
        // silently orphaned there (rebuilt cards never come from FullscreenCardHost).
        if (_expandedSlot != null) CollapseExpandedCard();

        RebuildChartsGridDefinitions();
        ChartsHost.Children.Clear();
        _slotElements.Clear();
        _sensorStates.Clear();

        var totalSensors = _dataService.Sensors.Count;

        if (totalSensors == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateLabel.Text = "ĐANG KẾT NỐI VỚI BACKEND...";
            SensorCountText.Text = "0";
            ChartCountBadge.Text = "0 / 0 hiển thị";
            TotalNodesText.Text = "0";
            ActiveAlertsText.Text = "0";
            SetConnectedStatus(false);
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        SetConnectedStatus(true);
        SensorCountText.Text = totalSensors.ToString();

        // Drop stale slot assignments whose sensor no longer exists in the topology.
        foreach (var slot in _slots)
        {
            if (slot.SensorId != null && _dataService.Sensors.All(s => s.SensorId != slot.SensorId))
                slot.SensorId = null;
        }

        var assignedIds = _slots.Where(s => s.SensorId != null).Select(s => s.SensorId!).ToHashSet();
        foreach (var staleId in _chartHistories.Keys.Where(id => !assignedIds.Contains(id)).ToList())
            _chartHistories.Remove(staleId);

        var assignedCount = 0;
        var alertCount = 0;

        foreach (var slot in _slots)
        {
            if (slot.IsHiddenBySpan) continue;

            var sensor = slot.SensorId != null ? _dataService.Sensors.FirstOrDefault(s => s.SensorId == slot.SensorId) : null;

            FrameworkElement cell;
            if (sensor != null)
            {
                assignedCount++;
                if (sensor.CurrentLevel >= SensorAlertLevel.Warning) alertCount++;

                var type = SensorTypeString(sensor.Category);
                var state = CreateChartState(sensor.SensorId!, slot, sensor.NodeName, type, sensor.CurrentValue);
                _sensorStates[sensor.SensorId] = state;
                cell = state.Card;
            }
            else
            {
                cell = BuildEmptySlot(slot);
            }

            Grid.SetRow(cell, slot.Row);
            Grid.SetColumn(cell, slot.Column);
            Grid.SetRowSpan(cell, slot.RowSpan);
            Grid.SetColumnSpan(cell, slot.ColumnSpan);

            _slotElements[slot] = cell;
            ChartsHost.Children.Add(cell);
        }

        var visibleSlotCount = _slots.Count(s => !s.IsHiddenBySpan);
        ChartCountBadge.Text = $"{assignedCount} / {visibleSlotCount} hiển thị";
        TotalNodesText.Text = assignedCount.ToString();
        ActiveAlertsText.Text = alertCount.ToString();
    }

    private Border BuildEmptySlot(ChartSlot slot)
    {
        var index = _slots.IndexOf(slot);
        var highlight = new Rectangle
        {
            Stroke          = ResolveBrush("DkBlueLightBrush", Color.FromArgb(255, 96, 165, 250)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX         = 12,
            RadiusY         = 12,
            Fill            = new SolidColorBrush(WithAlpha(ResolveColor("DkBlueLightBrush", Color.FromArgb(255, 96, 165, 250)), 40)),
            Opacity         = 0
        };

        var dashedBorder = new Rectangle
        {
            Stroke          = ResolveBrush("DkBorderBrush", Color.FromArgb(255, 45, 50, 56)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX         = 12,
            RadiusY         = 12
        };

        var iconWrap = new Border
        {
            CornerRadius        = new CornerRadius(4),
            Padding             = new Thickness(8, 3, 8, 3),
            Background          = ResolveBrush("DkInputBgBrush", Color.FromArgb(255, 17, 22, 33)),
            BorderBrush         = ResolveBrush("DkBorderBrush", Color.FromArgb(255, 45, 53, 69)),
            BorderThickness     = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text             = $"Ô {index + 1:00}",
                FontSize         = 10,
                FontWeight       = Microsoft.UI.Text.FontWeights.Bold,
                CharacterSpacing = 80,
                Foreground       = ResolveBrush("DkTextSecondaryBrush", Color.FromArgb(255, 148, 163, 184))
            }
        };

        var hint = new TextBlock
        {
            Text                = "Kéo cảm biến vào đây",
            FontSize            = 13,
            Foreground          = ResolveBrush("DkTextMutedBrush", Color.FromArgb(255, 150, 160, 179)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 10, 0, 4)
        };
        var label = new TextBlock
        {
            Text                = "Ô trống",
            FontSize            = 11,
            FontFamily          = new FontFamily("Segoe UI Variable"),
            Foreground          = ResolveBrush("DkTextFaintBrush", Color.FromArgb(255, 90, 97, 110)),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(iconWrap);
        stack.Children.Add(hint);
        stack.Children.Add(label);

        var inner = new Grid();
        inner.Children.Add(dashedBorder);
        inner.Children.Add(highlight);
        inner.Children.Add(stack);

        var placeholder = new Border
        {
            Child     = inner,
            AllowDrop = true
        };

        placeholder.DragOver += (_, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Hiển thị cảm biến ở đây";
            e.DragUIOverride.IsGlyphVisible = false;
        };
        placeholder.DragEnter += (_, _) => highlight.Opacity = 1;
        placeholder.DragLeave += (_, _) => highlight.Opacity = 0;
        placeholder.Drop += async (_, e) =>
        {
            highlight.Opacity = 0;
            if (!e.DataView.Contains(StandardDataFormats.Text)) return;
            var sensorId = await e.DataView.GetTextAsync();
            AssignSensorToSlot(_slots.IndexOf(slot), sensorId);
        };

        return placeholder;
    }

    private SensorChartState CreateChartState(
        string sensorId, ChartSlot slot, string nodeName, string type, double initialValue)
    {
        var chartColor  = ChartColor(type);
        var accentColor = AccentColor(type);

        if (!_chartHistories.TryGetValue(sensorId, out var chartValues))
        {
            chartValues = new ObservableCollection<double>(
                Enumerable.Repeat(initialValue, Math.Min(10, _historyLength)));
            _chartHistories[sensorId] = chartValues;
        }

        ISeries series;

        if (type == "vibration")
        {
            series = new ColumnSeries<double>
            {
                Values    = chartValues,
                Fill      = new SolidColorPaint(new SKColor(chartColor.Red, chartColor.Green, chartColor.Blue, 180)),
                Stroke    = new SolidColorPaint(SKColors.Transparent),
                MaxBarWidth       = 4,
                IgnoresBarPosition = true
            };
        }
        else
        {
            series = new LineSeries<double>
            {
                Values          = chartValues,
                Fill            = new SolidColorPaint(new SKColor(chartColor.Red, chartColor.Green, chartColor.Blue, 18)),
                Stroke          = new SolidColorPaint(chartColor) { StrokeThickness = 1.5f },
                GeometrySize    = 0,
                LineSmoothness  = 0.4
            };
        }

        var axisLabelColor = ResolveColor("DkChartAxisLabelBrush", Color.FromArgb(255, 100, 110, 130));
        var gridColor      = ResolveColor("DkChartGridBrush",      Color.FromArgb(200, 30,  42,  68));
        var axisLabelPaint = new SolidColorPaint(new SKColor(axisLabelColor.R, axisLabelColor.G, axisLabelColor.B));
        var gridPaint      = new SolidColorPaint(new SKColor(gridColor.R, gridColor.G, gridColor.B, gridColor.A)) { StrokeThickness = 1 };

        var chart = new CartesianChart
        {
            MinHeight       = 140,
            VerticalAlignment = VerticalAlignment.Stretch,
            Series          = new[] { series },
            AnimationsSpeed = TimeSpan.Zero,
            EasingFunction  = null,
            XAxes = new[] { new Axis { LabelsPaint = null, SeparatorsPaint = gridPaint, TextSize = 0 } },
            YAxes = new[] { BuildFixedYAxis(type, axisLabelPaint, gridPaint) }
        };

        var valueTb = new TextBlock
        {
            Text        = $"{initialValue:F1}",
            FontSize    = 32,
            FontFamily  = new FontFamily("Segoe UI Variable"),
            FontWeight  = Microsoft.UI.Text.FontWeights.Bold,
            Foreground  = ResolveBrush("DkChartValueBrush", Color.FromArgb(255, 218, 226, 253))
        };

        var statusDot = new Border
        {
            Width              = 6,
            Height             = 6,
            Background         = ResolveBrush("DkGreenBrush", Color.FromArgb(255, 63, 207, 142)),
            CornerRadius       = new CornerRadius(3),
            VerticalAlignment  = VerticalAlignment.Center
        };

        var statusTb = new TextBlock
        {
            Text              = "NORMAL",
            FontSize          = 8,
            FontFamily        = new FontFamily("Segoe UI Variable"),
            Foreground        = ResolveBrush("DkGreenBrush", Color.FromArgb(255, 63, 207, 142)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var card = BuildCard(sensorId, slot, nodeName, type, chart, valueTb, statusDot, statusTb, accentColor);

        return new SensorChartState
        {
            Card        = card,
            ChartValues = chartValues,
            ValueText   = valueTb,
            StatusDot   = statusDot,
            StatusText  = statusTb,
            LastValue   = initialValue
        };
    }

    // Fixed full-scale range per sensor type, so the Y axis never rescales when values move.
    // Types without an entry fall back to LiveCharts' automatic scaling.
    private static readonly Dictionary<string, (double Min, double Max, double Step)> YScaleByType = new()
    {
        ["temperature"] = (10,  50,   10),
        ["humidity"]    = (0,   100,  20),
        ["light"]       = (0,   500,  100),
        ["waterlevel"]  = (0,   3,    0.5),
        ["radar"]       = (0,   100,  25),
        ["infrared"]    = (0,   100,  25),
        ["vibration"]   = (0,   20,   5),
    };

    private static Axis BuildFixedYAxis(string type, SolidColorPaint labelPaint, SolidColorPaint gridPaint)
    {
        var axis = new Axis { LabelsPaint = labelPaint, SeparatorsPaint = gridPaint, TextSize = 9 };
        if (YScaleByType.TryGetValue(type, out var scale))
        {
            axis.MinLimit = scale.Min;
            axis.MaxLimit = scale.Max;
            axis.MinStep  = scale.Step;
            axis.ForceStepToMin = true;
        }
        return axis;
    }

    private Border BuildCard(
        string sensorId, ChartSlot slot, string nodeName, string type, CartesianChart chart,
        TextBlock valueTb, Border statusDot, TextBlock statusTb, Color accent)
    {
        var card = new Border
        {
            Background      = ResolveBrush("DkSurfaceBrush", Color.FromArgb(255, 23, 30, 51)),
            BorderBrush     = ResolveBrush("DkBorderBrush", Color.FromArgb(255, 45, 50, 56)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            AllowDrop       = true,
            CanDrag         = true
        };

        var content = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0 — header: type label + node name stacked, unassign button revealed on hover
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var typeLabel = new TextBlock
        {
            Text       = SensorLabel(type),
            FontSize   = 10,
            FontFamily = new FontFamily("Segoe UI Variable"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent)
        };

        var nameLabel = new TextBlock
        {
            Text         = nodeName,
            FontSize     = 10,
            Foreground   = new SolidColorBrush(WithAlpha(ResolveColor("DkTextSubtleBrush", Color.FromArgb(255, 194, 198, 214)), 200)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 2, 0, 0)
        };

        var headerText = new StackPanel { Orientation = Orientation.Vertical };
        headerText.Children.Add(typeLabel);
        headerText.Children.Add(nameLabel);
        Grid.SetColumn(headerText, 0);

        // Expand-to-fullscreen button — reuses the same glyph LiveVideoPage's camera-focus
        // "Phóng to" button uses, so the affordance reads consistently across pages.
        var expandIcon = new FontIcon
        {
            FontSize   = 10,
            Foreground = ResolveBrush("DkGrayBrush", Color.FromArgb(255, 123, 126, 133))
        };
        expandIcon.Glyph = "";
        var expandBtn = new Button
        {
            Content           = expandIcon,
            Background        = new SolidColorBrush(Colors.Transparent),
            BorderThickness   = new Thickness(0),
            Width             = 22,
            Height            = 22,
            Padding           = new Thickness(0),
            Opacity           = 0,
            VerticalAlignment = VerticalAlignment.Top
        };
        ToolTipService.SetToolTip(expandBtn, "Phóng to");
        expandBtn.Click += (_, _) => ExpandCard(slot);
        Grid.SetColumn(expandBtn, 1);

        var clearIcon = new FontIcon
        {
            Glyph      = "",
            FontSize   = 10,
            Foreground = ResolveBrush("DkGrayBrush", Color.FromArgb(255, 123, 126, 133))
        };
        var clearBtn = new Button
        {
            Content           = clearIcon,
            Background        = new SolidColorBrush(Colors.Transparent),
            BorderThickness   = new Thickness(0),
            Width             = 22,
            Height            = 22,
            Padding           = new Thickness(0),
            Opacity           = 0,
            VerticalAlignment = VerticalAlignment.Top
        };
        ToolTipService.SetToolTip(clearBtn, "Bỏ khỏi lưới hiển thị");
        clearBtn.Click += (_, _) => UnassignSlot(_slots.IndexOf(slot));
        Grid.SetColumn(clearBtn, 2);

        headerGrid.Children.Add(headerText);
        headerGrid.Children.Add(expandBtn);
        headerGrid.Children.Add(clearBtn);
        Grid.SetRow(headerGrid, 0);

        // Row 1 — chart
        Grid.SetRow(chart, 1);

        // Row 2 — value + status pill
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var valueStack = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 4,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        valueStack.Children.Add(valueTb);
        valueStack.Children.Add(new TextBlock
        {
            Text              = SensorUnit(type),
            FontSize          = 12,
            FontFamily        = new FontFamily("Segoe UI Variable"),
            Foreground        = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin            = new Thickness(2, 0, 0, 5)
        });
        Grid.SetColumn(valueStack, 0);

        var statusStack = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusStack.Children.Add(statusDot);
        statusStack.Children.Add(statusTb);

        var statusPill = new Border
        {
            Background        = ResolveBrush("DkInputBgBrush", Color.FromArgb(255, 11, 16, 32)),
            BorderBrush       = ResolveBrush("DkBorderBrush", Color.FromArgb(255, 45, 50, 56)),
            BorderThickness   = new Thickness(1),
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child             = statusStack
        };
        Grid.SetColumn(statusPill, 1);

        footer.Children.Add(valueStack);
        footer.Children.Add(statusPill);
        Grid.SetRow(footer, 2);

        content.Children.Add(headerGrid);
        content.Children.Add(chart);
        content.Children.Add(footer);

        // Overlay the edge resize grips on top of the card content — revealed on hover,
        // draggable to grow or shrink the card's column/row span by exactly one grid cell.
        // Only built when that direction is actually usable (can grow, or already spanned
        // so it can be dragged back) — same affordance rule as LiveVideoPage's
        // ShowRightGrip/ShowBottomGrip.
        var root = new Grid();
        root.Children.Add(content);

        var grips = new List<FrameworkElement>();
        var rightGrip = BuildEdgeGrip(slot, isRight: true);
        if (rightGrip != null) grips.Add(rightGrip);
        var bottomGrip = BuildEdgeGrip(slot, isRight: false);
        if (bottomGrip != null) grips.Add(bottomGrip);
        foreach (var grip in grips)
            root.Children.Add(grip);

        card.Child = root;

        card.PointerEntered += (_, _) =>
        {
            expandBtn.Opacity = 1;
            clearBtn.Opacity = 1;
            foreach (var grip in grips) grip.Opacity = 1;
        };
        card.PointerExited += (_, _) =>
        {
            expandBtn.Opacity = 0;
            clearBtn.Opacity = 0;
            foreach (var grip in grips) grip.Opacity = 0;
        };

        // Drag source: an already-displayed sensor can be dragged onto another slot to move it.
        // Only allowed when the drag started from the card's middle zone — grabbing near the
        // edges/corners is reserved for the resize grips instead of triggering a card move.
        var lastPressPoint = new Point();
        card.PointerPressed += (_, e) => lastPressPoint = e.GetCurrentPoint(card).Position;

        card.DragStarting += (_, e) =>
        {
            var marginX = card.ActualWidth  * (1 - CardDragZoneFraction) / 2;
            var marginY = card.ActualHeight * (1 - CardDragZoneFraction) / 2;
            var inDragZone =
                lastPressPoint.X >= marginX && lastPressPoint.X <= card.ActualWidth  - marginX &&
                lastPressPoint.Y >= marginY && lastPressPoint.Y <= card.ActualHeight - marginY;

            if (!inDragZone)
            {
                e.Cancel = true;
                return;
            }

            e.Data.SetText(sensorId);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            card.Opacity = 0.4;
        };
        card.DropCompleted += (_, _) => card.Opacity = 1.0;

        // Drop target: dropping a different sensor here replaces this card's assignment.
        card.DragOver += (_, e) =>
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Thay thế bằng cảm biến này";
            e.DragUIOverride.IsGlyphVisible = false;
        };
        card.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.Text)) return;
            var droppedId = await e.DataView.GetTextAsync();
            AssignSensorToSlot(_slots.IndexOf(slot), droppedId);
        };

        return card;
    }

    // ─────────── Fullscreen expand (mirrors LiveVideoPage's camera-focus mode) ───────────

    /// Detaches the given slot's card Border out of ChartsHost and re-parents it into
    /// FullscreenCardHost. No chart/series objects are recreated — it's the exact same
    /// CartesianChart bound to the exact same ObservableCollection, so the live tick loop
    /// keeps updating it untouched while it's shown fullscreen. The card never has an
    /// explicit Width/Height (it fills whatever Grid cell it's placed in via the default
    /// Stretch alignment), so re-parenting it needs no size bookkeeping at all.
    private void ExpandCard(ChartSlot slot)
    {
        if (slot == _expandedSlot) return;
        if (!_slotElements.TryGetValue(slot, out var element) || element is not Border card) return;

        if (_expandedSlot != null) CollapseExpandedCard();

        // Measure the card's real on-screen bounds while it's still sitting in ChartsHost —
        // once re-parented into FullscreenCardHost this position is gone, so this must run
        // first. PlayFullscreenTransition() then animates the card growing from exactly this
        // rect up to the fullscreen area, instead of just popping in already full-size.
        ComputeFullscreenFlipTransform(card);
        FullscreenCardTransform.ScaleX = _fsScaleX;
        FullscreenCardTransform.ScaleY = _fsScaleY;
        FullscreenCardTransform.TranslateX = _fsTranslateX;
        FullscreenCardTransform.TranslateY = _fsTranslateY;

        _expandedSlot = slot;
        ChartsHost.Children.Remove(card);
        FullscreenCardHost.Children.Add(card);

        // FullscreenOverlay's background is a translucent scrim (see XAML), so the grid
        // underneath is dimmed rather than hidden outright; pointer/wheel input still
        // naturally routes to the topmost overlay instead of the dimmed grid below it.
        FullscreenOverlay.Visibility = Visibility.Visible;
        PlayFullscreenTransition(opening: true, onCompleted: null);
    }

    /// Captures how much smaller/offset the card's current on-screen rect (inside ChartsHost)
    /// is relative to the fullscreen target rect (ChartsAreaRoot's bounds, inset by
    /// FullscreenOverlay's 28px Padding on every side), storing the result as a FLIP-style
    /// scale+translate pair in _fsScaleX/Y/_fsTranslateX/Y. ChartsAreaRoot is used as the
    /// common coordinate frame (rather than FullscreenOverlay itself) because it's always
    /// visible/laid out — FullscreenOverlay stays Collapsed until the moment this runs, so its
    /// own bounds aren't reliably measured yet.
    private void ComputeFullscreenFlipTransform(Border card)
    {
        const double overlayPadding = 28;

        var origin = card.TransformToVisual(ChartsAreaRoot).TransformPoint(new Point(0, 0));
        var targetWidth = Math.Max(1, ChartsAreaRoot.ActualWidth - overlayPadding * 2);
        var targetHeight = Math.Max(1, ChartsAreaRoot.ActualHeight - overlayPadding * 2);

        _fsScaleX = card.ActualWidth > 0 ? card.ActualWidth / targetWidth : 1;
        _fsScaleY = card.ActualHeight > 0 ? card.ActualHeight / targetHeight : 1;
        _fsTranslateX = origin.X - overlayPadding;
        _fsTranslateY = origin.Y - overlayPadding;
    }

    /// Reverses ExpandCard() instantly (no animation): moves the card back into ChartsHost
    /// at its slot's own grid position/span. Used defensively from BuildCharts() so a full
    /// rebuild never leaves the detached card orphaned in FullscreenCardHost — that path is
    /// a background rebuild, not a user-driven close, so it should not animate.
    private void CollapseExpandedCard()
    {
        if (_expandedSlot is not ChartSlot slot) return;

        if (FullscreenCardHost.Children.Count > 0 && FullscreenCardHost.Children[0] is Border card)
        {
            FullscreenCardHost.Children.Remove(card);
            Grid.SetRow(card, slot.Row);
            Grid.SetColumn(card, slot.Column);
            Grid.SetRowSpan(card, slot.RowSpan);
            Grid.SetColumnSpan(card, slot.ColumnSpan);
            ChartsHost.Children.Add(card);
        }

        FullscreenOverlay.Visibility = Visibility.Collapsed;
        _expandedSlot = null;
    }

    /// User-driven close: plays the fade/shrink-out transition first, then performs the
    /// actual re-parenting once it completes, so the card is still on screen while it
    /// visibly fades and shrinks rather than snapping away.
    private void CollapseExpandedCardAnimated()
    {
        if (_expandedSlot == null) return;
        PlayFullscreenTransition(opening: false, onCompleted: CollapseExpandedCard);
    }

    /// Fades the scrim in/out and, using the FLIP values ComputeFullscreenFlipTransform()
    /// captured at expand time, animates the card growing from its original grid-cell
    /// rect up to the fullscreen area (opening) or shrinking back down to it (closing) —
    /// a visibly larger, more obvious motion than a flat opacity/scale pop, since the card
    /// now appears to travel from exactly where the user clicked. Shared by ExpandCard
    /// (opening: true) and CollapseExpandedCardAnimated (opening: false, which runs the
    /// same values in reverse and defers onCompleted until the shrink finishes).
    private void PlayFullscreenTransition(bool opening, Action? onCompleted)
    {
        var duration = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn };

        var opacityAnim = MakeFullscreenAnim(opening ? 0 : 1, opening ? 1 : 0, duration, ease);
        Storyboard.SetTarget(opacityAnim, FullscreenOverlay);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");

        var scaleXAnim = MakeFullscreenAnim(opening ? _fsScaleX : 1, opening ? 1 : _fsScaleX, duration, ease);
        Storyboard.SetTarget(scaleXAnim, FullscreenCardTransform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");

        var scaleYAnim = MakeFullscreenAnim(opening ? _fsScaleY : 1, opening ? 1 : _fsScaleY, duration, ease);
        Storyboard.SetTarget(scaleYAnim, FullscreenCardTransform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");

        var translateXAnim = MakeFullscreenAnim(opening ? _fsTranslateX : 0, opening ? 0 : _fsTranslateX, duration, ease);
        Storyboard.SetTarget(translateXAnim, FullscreenCardTransform);
        Storyboard.SetTargetProperty(translateXAnim, "TranslateX");

        var translateYAnim = MakeFullscreenAnim(opening ? _fsTranslateY : 0, opening ? 0 : _fsTranslateY, duration, ease);
        Storyboard.SetTarget(translateYAnim, FullscreenCardTransform);
        Storyboard.SetTargetProperty(translateYAnim, "TranslateY");

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacityAnim);
        storyboard.Children.Add(scaleXAnim);
        storyboard.Children.Add(scaleYAnim);
        storyboard.Children.Add(translateXAnim);
        storyboard.Children.Add(translateYAnim);
        if (onCompleted != null)
            storyboard.Completed += (_, _) => onCompleted();
        storyboard.Begin();
    }

    private static DoubleAnimation MakeFullscreenAnim(double from, double to, TimeSpan duration, EasingFunctionBase ease) =>
        new() { From = from, To = to, Duration = duration, EasingFunction = ease };

    private void FullscreenClose_Click(object sender, RoutedEventArgs e) => CollapseExpandedCardAnimated();

    // ─────────── Edge resize grip (ratio-based) ───────────

    /// One edge drag handle: right edge grows/shrinks the card's column span, bottom edge
    /// grows/shrinks its row span — same affordance as LiveVideoPage's right/bottom grips.
    /// Dragging past half a cell toggles the span by exactly one whole cell; the decision
    /// is made once on ManipulationCompleted rather than accumulated pixel-by-pixel, so a
    /// card can only ever occupy a whole number of grid cells — a ratio of the available
    /// space, never a raw pixel amount, so the layout can't overflow the viewport the way
    /// free-form pixel resizing could. Returns null when this direction isn't usable (can't
    /// grow, and isn't already spanned) so BuildCard() simply omits the grip.
    private FrameworkElement? BuildEdgeGrip(ChartSlot slot, bool isRight)
    {
        var canGrow = isRight ? CanExpandRight(slot) : CanExpandDown(slot);
        var spanned = isRight ? slot.ColumnSpan > 1 : slot.RowSpan > 1;
        if (!canGrow && !spanned) return null;

        var barBrush = new SolidColorBrush(WithAlpha(ResolveColor("DkBlueLightBrush", Color.FromArgb(255, 96, 165, 250)), 230));
        Rectangle bar = isRight
            ? new Rectangle
            {
                Width = 3, Fill = barBrush, RadiusX = 1.5, RadiusY = 1.5, Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 24, 0, 24)
            }
            : new Rectangle
            {
                Height = 3, Fill = barBrush, RadiusX = 1.5, RadiusY = 1.5, Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(24, 0, 24, 0)
            };

        var grip = new CursorGrid
        {
            Background       = new SolidColorBrush(Colors.Transparent),
            Opacity          = 0,
            ManipulationMode = isRight ? ManipulationModes.TranslateX : ManipulationModes.TranslateY,
            Children         = { bar }
        };
        if (isRight)
        {
            grip.Width = 12;
            grip.HorizontalAlignment = HorizontalAlignment.Right;
            grip.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            grip.Height = 12;
            grip.HorizontalAlignment = HorizontalAlignment.Stretch;
            grip.VerticalAlignment = VerticalAlignment.Bottom;
        }

        var resizeCursor = InputSystemCursor.Create(isRight
            ? InputSystemCursorShape.SizeWestEast
            : InputSystemCursorShape.SizeNorthSouth);

        grip.PointerEntered += (_, _) => { grip.HoverCursor = resizeCursor; bar.Opacity = 1.0; };
        grip.PointerExited  += (_, _) => { grip.HoverCursor = null; bar.Opacity = 0.5; };

        double Threshold()
        {
            var cellSize = isRight
                ? ChartsHost.ActualWidth / Math.Max(1, _columns)
                : ChartsHost.ActualHeight / Math.Max(1, RowsPerLayout);
            return cellSize > 0 ? cellSize / 2 : 60;
        }

        var dragTotal = 0.0;
        var dimmedNeighbors = new List<FrameworkElement>();

        void ClearDim()
        {
            foreach (var el in dimmedNeighbors) el.Opacity = 1;
            dimmedNeighbors.Clear();
        }

        grip.ManipulationStarted += (_, _) => { dragTotal = 0; ClearDim(); };

        // Live preview while dragging (not a post-release animation): once the drag has
        // crossed the half-cell threshold, dim whichever neighbor cell(s) would actually get
        // absorbed if the grip were released right now — same set ExpandColumn()/ExpandRow()
        // would consume. Dropping back below the threshold (or reversing direction) restores
        // full opacity, tracking the live drag rather than committing anything.
        grip.ManipulationDelta += (_, e) =>
        {
            dragTotal += isRight ? e.Delta.Translation.X : e.Delta.Translation.Y;
            if (slot == _expandedSlot) return;

            var wantsExpand = dragTotal > Threshold() && (isRight ? CanExpandRight(slot) : CanExpandDown(slot));
            var targetNeighbors = wantsExpand
                ? NeighborsForExpand(slot, isRight)
                    .Select(n => _slotElements.TryGetValue(n, out var el) ? el : null)
                    .Where(el => el != null)
                    .Cast<FrameworkElement>()
                    .ToList()
                : new List<FrameworkElement>();

            foreach (var el in dimmedNeighbors)
                if (!targetNeighbors.Contains(el)) el.Opacity = 1;
            foreach (var el in targetNeighbors)
                el.Opacity = 0.35;
            dimmedNeighbors = targetNeighbors;
        };

        grip.ManipulationCompleted += (_, _) =>
        {
            ClearDim();

            // A no-op while this card is detached into the fullscreen overlay — its grip
            // travels with it there, but resizing a "fullscreen" card has no meaning.
            if (slot == _expandedSlot) return;

            var threshold = Threshold();
            if (dragTotal > threshold)
            {
                if (isRight) ExpandColumn(slot); else ExpandRow(slot);
            }
            else if (dragTotal < -threshold)
            {
                if (isRight) CollapseColumn(slot); else CollapseRow(slot);
            }
        };

        return grip;
    }

    private void SetConnectedStatus(bool connected)
    {
        if (connected)
        {
            ConnectionDot.Fill       = ResolveBrush("DkGreenBrush", Color.FromArgb(255, 63, 207, 142));
            ConnectionText.Text      = "ĐÃ KẾT NỐI";
            ConnectionText.Foreground = ResolveBrush("DkBlueLightBrush", Color.FromArgb(255, 96, 165, 250));
        }
        else
        {
            ConnectionDot.Fill       = ResolveBrush("DkConnectionOfflineBrush", Color.FromArgb(255, 61, 96, 112));
            ConnectionText.Text      = "ĐANG KẾT NỐI";
            ConnectionText.Foreground = ResolveBrush("DkConnectionOfflineBrush", Color.FromArgb(255, 61, 96, 112));
        }
    }

    // ─────────── Helpers ───────────

    private static string SensorTypeString(Models.AlertCategory cat) => cat switch
    {
        Models.AlertCategory.Temperature   => "temperature",
        Models.AlertCategory.Humidity      => "humidity",
        Models.AlertCategory.Light         => "light",
        Models.AlertCategory.WaterLevel    => "waterlevel",
        Models.AlertCategory.Radar         => "radar",
        Models.AlertCategory.Infrared      => "infrared",
        Models.AlertCategory.Accelerometer => "vibration",
        _                                  => "other"
    };

    // Derived from AccentColor(type) rather than a second hardcoded SKColor table —
    // same eight hues, just converted into SkiaSharp's color type for LiveCharts.
    private static SKColor ChartColor(string type)
    {
        var c = AccentColor(type);
        return new SKColor(c.R, c.G, c.B);
    }

    private static Color AccentColor(string type) => type switch
    {
        "temperature" => ResolveColor("SensorAccentTemperatureBrush", Color.FromArgb(255, 255, 77,  77)),
        "humidity"    => ResolveColor("MonitoringSensorHumidityBrush", Color.FromArgb(255, 34,  211, 238)),
        "light"       => ResolveColor("SensorAccentLightBrush", Color.FromArgb(255, 255, 184, 0)),
        "waterlevel"  => ResolveColor("SensorAccentWaterLevelBrush", Color.FromArgb(255, 80,  160, 255)),
        "radar"       => ResolveColor("SensorAccentRadarBrush", Color.FromArgb(255, 0,   255, 136)),
        "vibration"   => ResolveColor("MonitoringSensorVibrationBrush", Color.FromArgb(255, 132, 204, 22)),
        "infrared"    => ResolveColor("SensorAccentInfraredBrush", Color.FromArgb(255, 255, 105, 180)),
        _             => ResolveColor("SensorAccentDefaultBrush", Color.FromArgb(255, 173, 198, 255))
    };

    private static string SensorLabel(string type) => type switch
    {
        "temperature" => "NHIỆT ĐỘ",
        "humidity"    => "ĐỘ ẨM",
        "light"       => "ÁNH SÁNG",
        "waterlevel"  => "MỰC NƯỚC",
        "radar"       => "RADAR",
        "vibration"   => "RUNG ĐỘNG",
        "infrared"    => "HỒNG NGOẠI",
        _             => type.ToUpperInvariant()
    };

    private static string SensorUnit(string type) => type switch
    {
        "temperature" => "°C",
        "humidity"    => "%RH",
        "light"       => "lux",
        "waterlevel"  => "mm",
        "radar"       => "%",
        "infrared"    => "%",
        "vibration"   => "m/s²",
        _             => ""
    };

    // ─────────── Event handlers ───────────

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _totalUpdates = 0;
        TotalUpdatesText.Text = "0";
        foreach (var state in _sensorStates.Values)
            state.ChartValues.Clear();
    }

    // ─────────── Inner types ───────────

    private sealed class SensorChartState
    {
        public required Border Card { get; init; }
        public required ObservableCollection<double> ChartValues { get; init; }
        public required TextBlock ValueText { get; init; }
        public required Border StatusDot { get; init; }
        public required TextBlock StatusText { get; init; }
        public double LastValue { get; set; }
    }
}
