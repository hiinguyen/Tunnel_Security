using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Station.Models;
using Station.Services;
using Windows.UI;

namespace Station.ViewModels
{
    /// <summary>
    /// Worst-case view of one sensor category (e.g. all temperature sensors of the station).
    /// A station has dozens of sensors per category, so an average hides the one that matters.
    /// This shows the single worst online sensor (value, location, own history vs. its warning
    /// threshold) plus how many sensors of the category are over threshold.
    /// </summary>
    public sealed partial class CategoryMonitor : ObservableObject
    {
        private const int HistoryLength = 24;
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

        private readonly AlertCategory _category;
        private readonly string _valueFormat;
        private readonly ObservableCollection<ObservableValue> _values = new();
        private readonly ObservableCollection<ObservableValue> _threshold = new();
        private readonly Dictionary<string, List<double>> _history = new();
        private readonly Dictionary<string, DateTime> _lastSample = new();

        // Brushes are UI objects: created in the constructor, which runs on the UI thread.
        private readonly SolidColorBrush _normalBrush   = new(Color.FromArgb(255, 63, 207, 142));
        private readonly SolidColorBrush _warningBrush  = new(Color.FromArgb(255, 255, 209, 102));
        private readonly SolidColorBrush _criticalBrush = new(Color.FromArgb(255, 240, 98, 93));
        private readonly SolidColorBrush _offlineBrush  = new(Color.FromArgb(255, 123, 126, 133));
        private readonly SolidColorBrush _mutedBrush    = new(Color.FromArgb(255, 154, 166, 178));

        [ObservableProperty] private string valueText     = "-";
        [ObservableProperty] private string unitText      = string.Empty;
        [ObservableProperty] private string sourceText    = "Chưa có dữ liệu";
        [ObservableProperty] private string sourceTooltip = string.Empty;
        [ObservableProperty] private string countText     = string.Empty;
        [ObservableProperty] private string levelGlyph    = "○";
        [ObservableProperty] private string levelText     = "Không có dữ liệu";
        [ObservableProperty] private SolidColorBrush levelBrush;
        [ObservableProperty] private SolidColorBrush countBrush;

        /// <summary>Sparkline of the focus sensor plus a dashed warning-threshold line. Set once.</summary>
        public ISeries[] Series { get; }

        public CategoryMonitor(AlertCategory category, SKColor color, string valueFormat)
        {
            _category    = category;
            _valueFormat = valueFormat;
            levelBrush   = _offlineBrush;
            countBrush   = _mutedBrush;

            for (int i = 0; i < HistoryLength; i++)
            {
                _values.Add(new ObservableValue { Value = null });
                _threshold.Add(new ObservableValue { Value = null });
            }

            Series = new ISeries[]
            {
                new LineSeries<ObservableValue>
                {
                    Values         = _values,
                    Fill           = new SolidColorPaint(new SKColor(color.Red, color.Green, color.Blue, 30)),
                    Stroke         = new SolidColorPaint(color) { StrokeThickness = 2 },
                    GeometrySize   = 0,
                    LineSmoothness = 0,   // straight segments: smoothing can overshoot a real reading
                },
                new LineSeries<ObservableValue>
                {
                    Values         = _threshold,
                    Fill           = null,
                    GeometrySize   = 0,
                    LineSmoothness = 0,
                    Stroke         = new SolidColorPaint(new SKColor(0xFF, 0xD1, 0x66))
                    {
                        StrokeThickness = 1,
                        PathEffect      = new DashEffect(new[] { 4f, 3f })
                    },
                },
            };
        }

        /// <summary>Recompute the worst sensor and the over-threshold count from current readings.</summary>
        public void Refresh(IEnumerable<SimulatedSensor> allSensors)
        {
            var inCategory = allSensors.Where(s => s.Category == _category).ToList();
            var online     = inCategory.Where(s => s.IsOnline).ToList();

            if (online.Count == 0)
            {
                ShowNoOnlineSensor(inCategory.Count);
                return;
            }

            var focus = online.MaxBy(s => s.CurrentValue)!;
            EnsureHistory(focus);

            ValueText     = focus.CurrentValue.ToString(_valueFormat);
            UnitText      = " " + focus.Unit;
            SourceText    = focus.NodeName;
            SourceTooltip = $"{focus.SensorName} ({focus.SensorId})";

            int over = online.Count(s => s.CurrentLevel is SensorAlertLevel.Warning or SensorAlertLevel.Critical);
            CountText = $"{over} / {online.Count} vượt ngưỡng";

            var (glyph, text, brush) = Describe(focus.CurrentLevel);
            LevelGlyph = glyph;
            LevelText  = text;
            LevelBrush = brush;
            CountBrush = over > 0 ? brush : _mutedBrush;

            Repaint(focus);
        }

        /// <summary>Feed one live tick; only ticks of this category are used.</summary>
        public void OnTick(IEnumerable<SimulatedSensor> allSensors, SimulatedSensor ticked, double newValue)
        {
            if (ticked.Category != _category) return;

            var now = DateTime.UtcNow;
            if (!_lastSample.TryGetValue(ticked.SensorId, out var last) || now - last >= SampleInterval)
            {
                _lastSample[ticked.SensorId] = now;
                EnsureHistory(ticked);
                var list = _history[ticked.SensorId];
                list.Add(newValue);
                if (list.Count > HistoryLength) list.RemoveAt(0);
            }

            Refresh(allSensors);
        }

        private void EnsureHistory(SimulatedSensor s)
        {
            if (!_history.ContainsKey(s.SensorId))
                _history[s.SensorId] = new List<double> { s.CurrentValue };
        }

        // Rewrites the points in place (no collection changes), so LiveCharts only animates the diff.
        private void Repaint(SimulatedSensor focus)
        {
            var hist = _history[focus.SensorId];
            int pad  = HistoryLength - hist.Count;
            for (int i = 0; i < HistoryLength; i++)
            {
                _values[i].Value    = i < pad ? null : hist[i - pad];
                _threshold[i].Value = focus.WarnThreshold;
            }
        }

        private void ShowNoOnlineSensor(int total)
        {
            ValueText     = "-";
            UnitText      = string.Empty;
            SourceText    = total == 0 ? "Chưa có cảm biến" : "Mất kết nối";
            SourceTooltip = string.Empty;
            CountText     = total == 0 ? string.Empty : $"0 / {total} trực tuyến";
            LevelGlyph    = "○";
            LevelText     = "Mất kết nối";
            LevelBrush    = _offlineBrush;
            CountBrush    = _mutedBrush;
            for (int i = 0; i < HistoryLength; i++)
            {
                _values[i].Value    = null;
                _threshold[i].Value = null;
            }
        }

        // Shape + label reinforce the color so status never relies on hue alone.
        private (string Glyph, string Text, SolidColorBrush Brush) Describe(SensorAlertLevel level) => level switch
        {
            SensorAlertLevel.Critical => ("■", "Khẩn cấp",    _criticalBrush),
            SensorAlertLevel.Warning  => ("▲", "Cảnh báo",    _warningBrush),
            SensorAlertLevel.Offline  => ("○", "Mất kết nối", _offlineBrush),
            _                         => ("●", "Bình thường", _normalBrush),
        };
    }
}
