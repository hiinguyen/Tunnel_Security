using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Station.ViewModels;

namespace Station.Dialogs;

public sealed partial class EditNodeDialog : ContentDialog
{
    private readonly NodeItemViewModel _node;
    private readonly DevicesViewModel _viewModel;

    public EditNodeDialog(NodeItemViewModel node, DevicesViewModel viewModel)
    {
        this.InitializeComponent();
        RequestedTheme = Station.Services.ThemeService.Instance.CurrentTheme;
        _node = node;
        _viewModel = viewModel;

        LoadCurrentValues();
        this.PrimaryButtonClick += OnSaveClicked;
    }

    private void HeaderClose_Click(object sender, RoutedEventArgs e) => Hide();

    private void LoadCurrentValues()
    {
        NodeNameTextBox.Text = _node.NodeName;

        // Pre-select matching line in ComboBox
        for (int i = 0; i < LineComboBox.Items.Count; i++)
        {
            if (LineComboBox.Items[i] is ComboBoxItem item &&
                item.Content?.ToString() == _node.LineName)
            {
                LineComboBox.SelectedIndex = i;
                break;
            }
        }
        if (LineComboBox.SelectedItem == null && LineComboBox.Items.Count > 0)
            LineComboBox.SelectedIndex = 0;

        // Location = the part after "LineName / "
        var parts = _node.Location.Split('/');
        LocationTextBox.Text = parts.Length >= 2 ? parts[^1].Trim() : _node.Location;

        SensorCountInfoText.Text = $"Node có {_node.Sensors.Count} cảm biến – sẽ được cập nhật theo tên mới";
    }

    private void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = NodeNameTextBox.Text.Trim();
        var locationPart = LocationTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            args.Cancel = true;
            ShowError("Vui lòng nhập tên Node");
            return;
        }

        if (LineComboBox.SelectedItem == null)
        {
            args.Cancel = true;
            ShowError("Vui lòng chọn tuyến");
            return;
        }

        if (string.IsNullOrWhiteSpace(locationPart))
        {
            args.Cancel = true;
            ShowError("Vui lòng nhập vị trí");
            return;
        }

        var lineName = (LineComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? _node.LineName;
        var newLocation = $"{lineName} / {locationPart}";

        _viewModel.UpdateNode(_node, name, lineName, newLocation);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }
}
