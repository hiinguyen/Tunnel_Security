using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Station.ViewModels;
using Station.Models;
using System;

namespace Station.Dialogs
{
    public sealed partial class AddNodeDialog : ContentDialog
    {
   private DevicesViewModel _viewModel;

      public AddNodeDialog(DevicesViewModel viewModel)
        {
this.InitializeComponent();
RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
     _viewModel = viewModel;
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Validate required fields
 if (string.IsNullOrWhiteSpace(NodeNameTextBox.Text))
            {
        args.Cancel = true;
             ShowError("Vui l�ng nh?p t�n node");
        return;
         }

          if (LineComboBox.SelectedItem == null)
     {
     args.Cancel = true;
     ShowError("Vui l�ng ch?n tuy?n");
          return;
      }

        if (string.IsNullOrWhiteSpace(LocationTextBox.Text))
            {
   args.Cancel = true;
          ShowError("Vui l�ng nh?p v? tr�");
 return;
    }

        try
 {
                // Get selected line text
       var lineText = (LineComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unknown";

            // Create new node
   var newNode = new NodeItemViewModel
    {
           NodeName = NodeNameTextBox.Text.Trim(),
      LineName = lineText,
             Location = $"{LocationTextBox.Text.Trim()}",
         Status = DeviceStatus.Online
       };

   // Add 7 sensors with user-defined IDs
      var random = new Random();

 // 1. Radar ph�t hi?n ng??i
        newNode.Sensors.Add(new SensorItemViewModel
   {
       SensorId = string.IsNullOrWhiteSpace(RadarIdTextBox.Text) ? $"RAD-{newNode.NodeName}-001" : RadarIdTextBox.Text.Trim(),
       SensorName = "Radar ph�t hi?n ng??i",
     SensorType = "Radar Detection",
   CurrentValue = "Kh�ng ph�t hi?n",
         Unit = "",
     LastUpdateText = "V?a xong",
     SensorStatus = RadarStatusComboBox.SelectedIndex == 0 ? DeviceStatus.Online : DeviceStatus.Disabled,
TypeIcon = "\uE701",
       LineName = newNode.LineName,
        NodeName = newNode.NodeName,
            Location = newNode.Location
});

             // 2. Camera h?ng ngo?i
                newNode.Sensors.Add(new SensorItemViewModel
        {
          SensorId = string.IsNullOrWhiteSpace(CameraIdTextBox.Text) ? $"CAM-{newNode.NodeName}-001" : CameraIdTextBox.Text.Trim(),
             SensorName = "Camera h?ng ngo?i",
           SensorType = "Infrared Camera",
       CurrentValue = "Online",
  Unit = "",
    LastUpdateText = "V?a xong",
        SensorStatus = CameraStatusComboBox.SelectedIndex == 0 ? DeviceStatus.Online : DeviceStatus.Disabled,
    TypeIcon = "\uE714",
      LineName = newNode.LineName,
  NodeName = newNode.NodeName,
Location = newNode.Location
      });

// 3. C?m bi?n h?ng ngo?i (PIR)
              newNode.Sensors.Add(new SensorItemViewModel
       {
              SensorId = string.IsNullOrWhiteSpace(PirIdTextBox.Text) ? $"PIR-{newNode.NodeName}-001" : PirIdTextBox.Text.Trim(),
      SensorName = "C?m bi?n h?ng ngo?i",
           SensorType = "PIR Motion Sensor",
           CurrentValue = "Kh�ng chuy?n ??ng",
  Unit = "",
        LastUpdateText = "V?a xong",
  SensorStatus = PirStatusComboBox.SelectedIndex == 0 ? DeviceStatus.Online : DeviceStatus.Disabled,
           TypeIcon = "\uE7C1",
       LineName = newNode.LineName,
    NodeName = newNode.NodeName,
         Location = newNode.Location
            });

         // 4. C?m bi?n nhi?t ?? & ?? ?m
         newNode.Sensors.Add(new SensorItemViewModel
  {
        SensorId = string.IsNullOrWhiteSpace(TempHumidIdTextBox.Text) ? $"THM-{newNode.NodeName}-001" : TempHumidIdTextBox.Text.Trim(),
              SensorName = "C?m bi?n nhi?t ?? & ?? ?m",
               SensorType = "Temperature & Humidity Sensor",
    CurrentValue = $"{20 + random.Next(15)}.{random.Next(10)}�C / {40 + random.Next(40)}%",
        Unit = "",
            LastUpdateText = "V?a xong",
         SensorStatus = TempHumidStatusComboBox.SelectedIndex == 0 ? DeviceStatus.Online : DeviceStatus.Disabled,
    TypeIcon = "\uE9CA",
  LineName = newNode.LineName,
       NodeName = newNode.NodeName,
           Location = newNode.Location
          });

    // 5. C?m bi?n �nh s�ng
  newNode.Sensors.Add(new SensorItemViewModel
   {
    SensorId = string.IsNullOrWhiteSpace(LightIdTextBox.Text) ? $"LUX-{newNode.NodeName}-001" : LightIdTextBox.Text.Trim(),
            SensorName = "C?m bi?n �nh s�ng",
        SensorType = "Light Sensor",
        CurrentValue = $"{100 + random.Next(400)}",
        Unit = "lux",
LastUpdateText = "V?a xong",
  SensorStatus = LightStatusComboBox.SelectedIndex == 0 ? DeviceStatus.Online : DeviceStatus.Disabled,
            TypeIcon = "\uE706",
                    LineName = newNode.LineName,
          NodeName = newNode.NodeName,
   Location = newNode.Location
           });

         // 6. C?m bi?n m?c n??c
        newNode.Sensors.Add(new SensorItemViewModel
          {
        SensorId = string.IsNullOrWhiteSpace(WaterIdTextBox.Text) ? $"WTR-{newNode.NodeName}-001" : WaterIdTextBox.Text.Trim(),
        SensorName = "C?m bi?n m?c n??c",
     SensorType = "Water Level Sensor",
          CurrentValue = $"{random.Next(50, 150)}.{random.Next(10)}",
 Unit = "cm",
              LastUpdateText = "V?a xong",
     SensorStatus = WaterStatusComboBox.SelectedIndex == 0 ? DeviceStatus.Online : DeviceStatus.Disabled,
         TypeIcon = "\uE9F2",
   LineName = newNode.LineName,
   NodeName = newNode.NodeName,
      Location = newNode.Location
      });

        // 7. C?m bi?n gia t?c (rung ??ng)
       newNode.Sensors.Add(new SensorItemViewModel
     {
        SensorId = string.IsNullOrWhiteSpace(AccelerometerIdTextBox.Text) ? $"ACC-{newNode.NodeName}-001" : AccelerometerIdTextBox.Text.Trim(),
  SensorName = "C?m bi?n gia t?c",
     SensorType = "Accelerometer Sensor",
            CurrentValue = $"{random.Next(1, 10)}.{random.Next(10)}",
   Unit = "m/s�",
         LastUpdateText = "V?a xong",
    SensorStatus = AccelerometerStatusComboBox.SelectedIndex == 0 ? DeviceStatus.Online : DeviceStatus.Disabled,
              TypeIcon = "\uEDA4",
                 LineName = newNode.LineName,
   NodeName = newNode.NodeName,
      Location = newNode.Location
       });

                // Register node – updates stats and respects active filters
                _viewModel.RegisterNewNode(newNode);

                System.Diagnostics.Debug.WriteLine($"Node '{newNode.NodeName}' with 7 sensors added successfully!");
            }
 catch (Exception ex)
            {
        args.Cancel = true;
      ShowError($"L?i khi th�m node: {ex.Message}");
       System.Diagnostics.Debug.WriteLine($"Error adding node: {ex.Message}");
            }
        }

        private async void ShowError(string message)
        {
  var errorDialog = new ContentDialog
    {
        Title = "L?i",
      Content = message,
      CloseButtonText = "OK",
  XamlRoot = this.XamlRoot
    };

     await errorDialog.ShowAsync();
        }
    }
}
