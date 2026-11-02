namespace AlertInjector;

/// <summary>Cua so nho luon noi tren: chon node, cam bien, muc canh bao roi bom gia tri vuot nguong vao NodePublisherSim.</summary>
public sealed class MainForm : Form
{
    private static readonly (string Key, string Name, string Unit)[] Sensors =
    [
        ("light", "Ánh sáng", "lx"),
        ("water", "Mực nước", "m"),
        ("temp", "Nhiệt độ", "°C"),
        ("hum", "Độ ẩm", "%"),
        ("radar", "Radar (số đối tượng)", "đối tượng")
    ];

    private readonly SimClient _sim;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    private readonly Label _status = new() { AutoSize = true };
    private readonly ComboBox _node = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _sensor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RadioButton _warning = new() { Text = "Cảnh báo", Checked = true, AutoSize = true };
    private readonly RadioButton _critical = new() { Text = "Nguy hiểm", AutoSize = true };
    private readonly CheckBox _customValue = new() { Text = "Giá trị tùy chỉnh", AutoSize = true };
    private readonly NumericUpDown _value = new() { DecimalPlaces = 1, Minimum = -1000, Maximum = 10000, Value = 0, Enabled = false };
    private readonly NumericUpDown _duration = new() { Minimum = 0, Maximum = 3600, Value = 30 };
    private readonly Button _fire = new() { Text = "Phát cảnh báo", AutoSize = true, Height = 34 };
    private readonly Button _clearNode = new() { Text = "Xóa node này", AutoSize = true, Height = 34 };
    private readonly Button _clearAll = new() { Text = "Xóa tất cả", AutoSize = true, Height = 34 };
    private readonly ListView _active = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };

    private List<NodeInfo> _joined = [];
    private bool _refreshing;

    public MainForm(SimClient sim)
    {
        _sim = sim;
        Text = "Phát cảnh báo giả";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(480, 580);
        MinimumSize = new Size(440, 540);
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Screen.PrimaryScreen!.WorkingArea.Right - Width - 24, 80);

        _sensor.Items.AddRange(Sensors.Select(s => (object)s.Name).ToArray());
        _sensor.SelectedIndex = 0;
        foreach (var (title, width) in new[] { ("Node", 190), ("Cảm biến", 100), ("Giá trị", 80), ("Còn lại", 80) })
        {
            _active.Columns.Add(title, width);
        }

        BuildLayout();

        _customValue.CheckedChanged += (_, _) => _value.Enabled = _customValue.Checked;
        _fire.Click += async (_, _) => await RunAsync(FireAsync);
        _clearNode.Click += async (_, _) => await RunAsync(() => SelectedNode is { } n ? _sim.ClearAsync(n) : Task.CompletedTask);
        _clearAll.Click += async (_, _) => await RunAsync(_sim.ClearAllAsync);
        _active.DoubleClick += async (_, _) => await RunAsync(ClearSelectedRowAsync);
        _poll.Tick += async (_, _) => await RefreshAsync();
        _poll.Start();
        Shown += async (_, _) => await RefreshAsync();
    }

    private NodeInfo? SelectedNode => _node.SelectedIndex >= 0 && _node.SelectedIndex < _joined.Count ? _joined[_node.SelectedIndex] : null;

    private void BuildLayout()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
            AutoScroll = true
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 8) });
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 4, 0, 4);
            grid.Controls.Add(control);
        }

        void Span(Control control)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(control);
            grid.SetColumnSpan(control, 2);
        }

        _status.Margin = new Padding(0, 0, 0, 8);
        Span(_status);

        Row("Node", _node);
        Row("Cảm biến", _sensor);
        Row("Mức", new FlowLayoutPanel { AutoSize = true, Controls = { _warning, _critical } });
        Row("Giá trị", new FlowLayoutPanel { AutoSize = true, Controls = { _customValue, _value } });
        Row("Kéo dài (giây)", _duration);
        Span(new Label { Text = "0 = giữ đến khi xóa", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(120, 0, 0, 8) });

        _fire.BackColor = Color.FromArgb(0xC6, 0x28, 0x28);
        _fire.ForeColor = Color.White;
        _fire.FlatStyle = FlatStyle.Flat;
        _fire.FlatAppearance.BorderSize = 0;
        Span(new FlowLayoutPanel { AutoSize = true, Controls = { _fire, _clearNode, _clearAll }, Margin = new Padding(0, 8, 0, 12) });

        Span(new Label { Text = "Đang phát (nhấp đúp để xóa)", AutoSize = true, Margin = new Padding(0, 0, 0, 4) });

        _active.Dock = DockStyle.Fill;
        _active.Height = 170;
        Span(_active);

        Controls.Add(grid);
    }

    private async Task FireAsync()
    {
        if (SelectedNode is not { } node)
        {
            throw new InvalidOperationException("Chưa chọn node (chờ node join vào gateway).");
        }

        var sensor = Sensors[_sensor.SelectedIndex].Key;
        var level = _critical.Checked ? "critical" : "warning";
        double? value = _customValue.Checked ? (double)_value.Value : null;
        await _sim.InjectAsync(node, sensor, level, value, (int)_duration.Value);
    }

    private Task ClearSelectedRowAsync()
    {
        if (_active.SelectedItems.Count == 0 || _active.SelectedItems[0].Tag is not (NodeInfo node, string sensor))
        {
            return Task.CompletedTask;
        }

        return _sim.ClearAsync(node, sensor);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var nodes = await _sim.GetNodesAsync();
            _joined = nodes.Where(n => n.Joined).ToList();
            _status.Text = $"Đã kết nối simulator - {_joined.Count}/{nodes.Count} node đã join";
            _status.ForeColor = Color.FromArgb(0x2E, 0x7D, 0x32);
            SyncNodeList();
            SyncActiveList();
        }
        catch
        {
            _status.Text = "Không kết nối được NodePublisherSim (127.0.0.1)";
            _status.ForeColor = Color.FromArgb(0xC6, 0x28, 0x28);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SyncNodeList()
    {
        var labels = _joined.Select(n => n.Label).ToArray();
        if (_node.Items.Cast<string>().SequenceEqual(labels))
        {
            return;
        }

        var keep = _node.SelectedItem as string;
        _node.Items.Clear();
        _node.Items.AddRange(labels.Cast<object>().ToArray());
        var index = keep is null ? -1 : Array.IndexOf(labels, keep);
        _node.SelectedIndex = index >= 0 ? index : (labels.Length > 0 ? 0 : -1);
    }

    private void SyncActiveList()
    {
        _active.BeginUpdate();
        _active.Items.Clear();
        foreach (var node in _joined)
        {
            foreach (var o in node.Overrides)
            {
                var (_, name, unit) = Sensors.First(s => s.Key == o.Sensor);
                var item = new ListViewItem(node.DisplayName) { Tag = (node, o.Sensor) };
                item.SubItems.Add(name);
                item.SubItems.Add($"{o.Value:0.##} {unit}");
                item.SubItems.Add(o.SecondsLeft is { } s ? $"{s:0}s" : "đến khi xóa");
                _active.Items.Add(item);
            }
        }

        _active.EndUpdate();
    }
}
