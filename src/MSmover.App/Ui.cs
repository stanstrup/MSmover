namespace MSmover.App;

/// <summary>Small helpers for building the two-column settings grids by hand.</summary>
internal static class Ui
{
    public static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 4, 4, 12),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    public static void Section(TableLayoutPanel t, string title)
    {
        var label = new Label
        {
            Text = title.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = Color.FromArgb(0x2F, 0x7A, 0xD1),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Margin = new Padding(0, t.RowCount == 0 ? 4 : 16, 0, 4)
        };
        t.Controls.Add(label);
        t.SetColumnSpan(label, 2);
    }

    public static T Row<T>(TableLayoutPanel t, string label, T control, string? hint = null) where T : Control
    {
        t.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 3)
        });

        control.Margin = new Padding(0, 3, 8, 3);
        if (control is TextBox or ComboBox) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        if (hint is null)
        {
            t.Controls.Add(control);
        }
        else
        {
            var host = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            host.Controls.Add(control);
            host.Controls.Add(new Label
            {
                Text = hint, AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(6, 7, 0, 0)
            });
            t.Controls.Add(host);
        }
        return control;
    }

    public static T Full<T>(TableLayoutPanel t, T control) where T : Control
    {
        control.Margin = new Padding(0, 3, 8, 3);
        t.Controls.Add(control);
        t.SetColumnSpan(control, 2);
        return control;
    }

    public static CheckBox Check(string text, bool value) =>
        new() { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 4, 12, 4) };

    /// <summary>
    /// Order matters: NumericUpDown validates Value against the range that is set at the time of
    /// assignment, and the defaults are 0..100. Maximum and Minimum must both be widened first.
    /// </summary>
    public static NumericUpDown Num(decimal value, decimal min, decimal max, int width = 90) =>
        new() { Maximum = max, Minimum = min, Value = Math.Clamp(value, min, max), Width = width };

    public static ComboBox Combo<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var v in Enum.GetValues<TEnum>()) c.Items.Add(v);
        c.SelectedItem = value;
        return c;
    }

    public static string? BrowseFolder(string? current, string description)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.SelectedPath = current;
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
