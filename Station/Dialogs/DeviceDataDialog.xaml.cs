using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Station.ViewModels;
using Station.Models;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Station.Dialogs;

public sealed partial class DeviceDataDialog : ContentDialog
{
    private readonly DeviceItemViewModel _device;

    // Temperature Chart Properties
    public ISeries[] TemperatureSeries { get; set; } = Array.Empty<ISeries>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> TemperatureXAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> TemperatureYAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();

    // Humidity Chart Properties
    public ISeries[] HumiditySeries { get; set; } = Array.Empty<ISeries>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> HumidityXAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> HumidityYAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();

    // Vibration Chart Properties
    public ISeries[] VibrationSeries { get; set; } = Array.Empty<ISeries>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> VibrationXAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> VibrationYAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();

    // Motion Chart Properties
    public ISeries[] MotionSeries { get; set; } = Array.Empty<ISeries>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> MotionXAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();
    public IEnumerable<LiveChartsCore.Kernel.Sketches.ICartesianAxis> MotionYAxes { get; set; } = Array.Empty<LiveChartsCore.Kernel.Sketches.ICartesianAxis>();

    public DeviceDataDialog(DeviceItemViewModel device)
    {
        _device = device;
   
        this.InitializeComponent();
        RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
        
 InitializeCharts();
        LoadDeviceInfo();
    LoadMockData();

        // Handle time range change
        TimeRangeComboBox.SelectionChanged += TimeRangeComboBox_SelectionChanged;
    }

    // Constructor for Relay Station by ID
    public DeviceDataDialog(string relayId)
    {
        // Create a mock device for relay station
        _device = new DeviceItemViewModel
        {
            DeviceId = relayId,
            Name = GetRelayName(relayId),
            Type = "relay",
            Status = DeviceStatus.Online,
            Location = GetRelayLocation(relayId),
            LastOnline = DateTimeOffset.Now
        };

        this.InitializeComponent();
        RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
        
        InitializeCharts();
        LoadDeviceInfo();
        LoadMockData();

        // Handle time range change
        TimeRangeComboBox.SelectionChanged += TimeRangeComboBox_SelectionChanged;
    }

    private string GetRelayName(string relayId)
    {
        return relayId switch
        {
            "RELAY_A" => "Điểm Trung Chuyển Khu A",
            "RELAY_B" => "Điểm Trung Chuyển Khu B",
            "RELAY_C" => "Điểm Trung Chuyển Khu C",
            _ => "Điểm Trung Chuyển"
        };
    }

    private string GetRelayLocation(string relayId)
    {
        return relayId switch
        {
            "RELAY_A" => "Khu vực A",
            "RELAY_B" => "Khu vực B",
            "RELAY_C" => "Khu vực C",
            _ => "Không xác định"
        };
    }

    private void HeaderClose_Click(object sender, RoutedEventArgs e) => Hide();

    private void InitializeCharts()
    {
        // ===== TEMPERATURE CHART =====
    var temperatureData = GenerateMockTemperatureData();
    
   TemperatureSeries = new ISeries[]
    {
            new LineSeries<double>
          {
       Name = "Nhiệt độ",
 Values = temperatureData,
       Fill = new LinearGradientPaint(
       new SKColor(255, 77, 77, 60),  // sensor accent with opacity
 new SKColor(255, 77, 77, 10),
          new SKPoint(0, 0),
        new SKPoint(0, 1)),
        Stroke = new SolidColorPaint(new SKColor(255, 77, 77)) { StrokeThickness = 2 },
      GeometrySize = 8,
      GeometryStroke = new SolidColorPaint(new SKColor(255, 77, 77)) { StrokeThickness = 2 },
                GeometryFill = new SolidColorPaint(new SKColor(255, 255, 255)),
          LineSmoothness = 0.5
         }
 };

        TemperatureXAxes = new Axis[]
   {
            new Axis
       {
       Labels = new string[] { "00h", "04h", "08h", "12h", "16h", "20h", "24h" },
LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
     SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
      TextSize = 11
       }
        };

        TemperatureYAxes = new Axis[]
        {
    new Axis
            {
    LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
    SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
             TextSize = 11,
                MinLimit = 20,
       MaxLimit = 40
    }
        };

  // ===== HUMIDITY CHART =====
        var humidityData = GenerateMockHumidityData();
      
      HumiditySeries = new ISeries[]
        {
   new LineSeries<double>
         {
  Name = "Độ ẩm",
  Values = humidityData,
         Fill = new LinearGradientPaint(
 new SKColor(80, 160, 255, 60),  // sensor accent with opacity
            new SKColor(80, 160, 255, 10),
        new SKPoint(0, 0),
      new SKPoint(0, 1)),
 Stroke = new SolidColorPaint(new SKColor(80, 160, 255)) { StrokeThickness = 2 },
  GeometrySize = 8,
      GeometryStroke = new SolidColorPaint(new SKColor(80, 160, 255)) { StrokeThickness = 2 },
 GeometryFill = new SolidColorPaint(new SKColor(255, 255, 255)),
     LineSmoothness = 0.5
       }
      };

        HumidityXAxes = new Axis[]
        {
    new Axis
        {
        Labels = new string[] { "00h", "04h", "08h", "12h", "16h", "20h", "24h" },
          LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
  SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
         TextSize = 11
            }
 };

    HumidityYAxes = new Axis[]
   {
     new Axis
    {
         LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
     SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
         TextSize = 11,
MinLimit = 60,
 MaxLimit = 100
   }
      };

        // ===== VIBRATION CHART =====
     var vibrationData = GenerateMockVibrationData();
        
VibrationSeries = new ISeries[]
        {
            new LineSeries<double>
    {
           Name = "Rung động",
         Values = vibrationData,
    Fill = new LinearGradientPaint(
    new SKColor(255, 184, 0, 60),  // sensor accent with opacity
        new SKColor(255, 184, 0, 10),
        new SKPoint(0, 0),
       new SKPoint(0, 1)),
       Stroke = new SolidColorPaint(new SKColor(255, 184, 0)) { StrokeThickness = 2 },
     GeometrySize = 8,
             GeometryStroke = new SolidColorPaint(new SKColor(255, 184, 0)) { StrokeThickness = 2 },
     GeometryFill = new SolidColorPaint(new SKColor(255, 255, 255)),
     LineSmoothness = 0.5
        }
        };

        VibrationXAxes = new Axis[]
        {
        new Axis
    {
    Labels = new string[] { "00h", "04h", "08h", "12h", "16h", "20h", "24h" },
   LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
  SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
     TextSize = 11
}
        };

        VibrationYAxes = new Axis[]
        {
new Axis
      {
      LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
   SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
      TextSize = 11,
       MinLimit = 0,
     MaxLimit = 5
            }
     };

        // ===== MOTION CHART =====
        var motionData = GenerateMockMotionData();
        
     MotionSeries = new ISeries[]
        {
        new ColumnSeries<double>
            {
     Name = "Chuyển động",
            Values = motionData,
     Fill = new SolidColorPaint(new SKColor(0, 255, 136)),  
    MaxBarWidth = 40
            }
        };

        MotionXAxes = new Axis[]
        {
            new Axis
            {
     Labels = new string[] { "00h", "04h", "08h", "12h", "16h", "20h", "24h" },
  LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
  SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
              TextSize = 11
            }
 };

        MotionYAxes = new Axis[]
   {
   new Axis
     {
                LabelsPaint = new SolidColorPaint(new SKColor(148, 163, 184)),
       SeparatorsPaint = new SolidColorPaint(new SKColor(45, 50, 56)) { StrokeThickness = 1 },
       TextSize = 11,
       MinLimit = 0
  }
        };
    }

    private List<double> GenerateMockTemperatureData()
    {
        // Mock temperature data for 24 hours (7 data points)
        var random = new Random(42);
        var data = new List<double>();
      var baseTemp = 30.0;
        
        for (int i = 0; i < 24; i++)
        {
// Simulate daily temperature pattern
       var hourFactor = Math.Sin((i - 6) * Math.PI / 12) * 5; // Peak around 2 PM
          var randomVariation = (random.NextDouble() - 0.5) * 2;
       data.Add(baseTemp + hourFactor + randomVariation);
   }
        
 return data;
    }

    private List<double> GenerateMockHumidityData()
    {
        // Mock humidity data for 24 hours
        var random = new Random(43);
        var data = new List<double>();
     var baseHumidity = 85.0;
        
for (int i = 0; i < 24; i++)
        {
 // Inverse of temperature pattern
     var hourFactor = -Math.Sin((i - 6) * Math.PI / 12) * 8;
            var randomVariation = (random.NextDouble() - 0.5) * 3;
          data.Add(Math.Clamp(baseHumidity + hourFactor + randomVariation, 70, 95));
        }
    
        return data;
    }

    private List<double> GenerateMockVibrationData()
    {
  // Mock vibration data with some spikes
        var random = new Random(44);
        var data = new List<double>();
      
  for (int i = 0; i < 24; i++)
        {
  var baseVibration = 1.5;
            var randomSpike = random.NextDouble() < 0.2 ? random.NextDouble() * 2 : 0;
     var randomVariation = (random.NextDouble() - 0.5) * 0.5;
            data.Add(Math.Clamp(baseVibration + randomSpike + randomVariation, 0.5, 4.5));
        }
   
        return data;
    }

 private List<double> GenerateMockMotionData()
    {
        // Mock motion event counts
    var random = new Random(45);
        var data = new List<double>();
        
        for (int i = 0; i < 24; i++)
        {
    // More events during day hours (6-22)
   var isDay = i >= 6 && i <= 22;
            var baseEvents = isDay ? 3 : 1;
         var randomEvents = random.Next(0, isDay ? 5 : 3);
            data.Add(baseEvents + randomEvents);
        }

   return data;
    }

    private void LoadDeviceInfo()
    {
        DeviceNameText.Text = _device.Name;
        DeviceIdText.Text = _device.DeviceId;
        LocationText.Text = _device.Location;

   // Update title
        this.Title = $"Dữ liệu đo - {_device.Name}";
    }

    private void LoadMockData()
  {
     // Calculate statistics from generated data
        var tempData = GenerateMockTemperatureData();
  var humidData = GenerateMockHumidityData();
        var vibData = GenerateMockVibrationData();
        var motionData = GenerateMockMotionData();

     // Temperature statistics
        TemperatureValue.Text = tempData[tempData.Count - 1].ToString("F1");
 TempMin.Text = $"{tempData.Min():F1}°C";
        TempAvg.Text = $"{tempData.Average():F1}°C";
        TempMax.Text = $"{tempData.Max():F1}°C";

        // Humidity statistics
        HumidityValue.Text = humidData[humidData.Count - 1].ToString("F0");
        HumidMin.Text = $"{humidData.Min():F0}%";
     HumidAvg.Text = $"{humidData.Average():F0}%";
 HumidMax.Text = $"{humidData.Max():F0}%";

     // Vibration statistics
        VibrationValue.Text = vibData[vibData.Count - 1].ToString("F1");
        VibMin.Text = $"{vibData.Min():F1} mm/s";
        VibAvg.Text = $"{vibData.Average():F1} mm/s";
        VibMax.Text = $"{vibData.Max():F1} mm/s";

        // Motion statistics
        var totalMotion = (int)motionData.Sum();
        MotionValue.Text = ((int)motionData[motionData.Count - 1]).ToString();
        MotionToday.Text = totalMotion.ToString();
MotionWeek.Text = (totalMotion * 7).ToString();
        MotionMonth.Text = (totalMotion * 30).ToString();
    }

    private void TimeRangeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeRangeComboBox.SelectedIndex == -1) return;

        var selectedItem = TimeRangeComboBox.SelectedItem as ComboBoxItem;
        var range = selectedItem?.Content.ToString();

        System.Diagnostics.Debug.WriteLine($"Time range changed to: {range}");

        // TODO: Reload data based on selected time range
      // For now, we just use the same mock data
        // In a real app, you would call API to fetch data for the selected range
    }
}
