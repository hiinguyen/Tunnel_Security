using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Station.ViewModels;

namespace Station.Dialogs
{
    public sealed partial class SensorDetailDialog : ContentDialog
    {
        public SensorItemViewModel Sensor { get; }

        public SensorDetailDialog(SensorItemViewModel sensor)
        {
            this.InitializeComponent();
            RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
            Sensor = sensor;
        }

        private void HeaderClose_Click(object sender, RoutedEventArgs e) => Hide();
    }
}
