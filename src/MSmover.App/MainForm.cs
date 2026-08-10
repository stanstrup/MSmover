using System.Diagnostics;
using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Engine;
using MSmover.Core.Logging;
using MSmover.Core.Transfer;

namespace MSmover.App;

public sealed class MainForm : Form
{
    private readonly MoverService _service;

    private readonly ListView _rulesList = new();
    private readonly ListView _queueList = new();
    private readonly RichTextBox _logBox = new();
    private readonly ComboBox _logLevelFilter = new();
    private readonly TextBox _logSearch = new();
    private readonly CheckBox _autoScroll = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripButton _pauseButton = new();
    private readonly ToolStripButton _dryRunButton = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private SettingsPanel _settings = null!;
    private long _logSeq;
    private bool _suspendUi;

    public MainForm(MoverService service)
    {
        _service = service;

        Text = "MSmover";
        Icon = TrayIcons.For(ServiceHealth.Idle);
        MinimumSize = new Size(900, 520);
        Size = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        RefreshAll();
        AppendNewLogEntries();

        _timer.Interval = 1000;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // =============================================================== layout

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildRulesTab());
        tabs.TabPages.Add(BuildQueueTab());
        tabs.TabPages.Add(BuildLogTab());
        tabs.TabPages.Add(BuildSettingsTab());

        var status = new StatusStrip();
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        status.Items.Add(_statusLabel);

        Controls.Add(tabs);
        Controls.Add(BuildToolbar());
        Controls.Add(status);
    }

    private ToolStrip BuildToolbar()
    {
        _pauseButton.Text = "Pause";
        _pauseButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _pauseButton.Click += (_, _) => { _service.SetPaused(!_service.Paused); SaveConfig(); RefreshAll(); };

        _dryRunButton.Text = "Global dry run";
        _dryRunButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _dryRunButton.CheckOnClick = false;
        _dryRunButton.Click += (_, _) => ToggleGlobalDryRun();

        var scan = new ToolStripButton("Scan now") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        scan.Click += (_, _) => { _service.ScanNow(); RefreshAll(); };

        return new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            Items = { _pauseButton, _dryRunButton, new ToolStripSeparator(), scan }
        };
    }

    // --------------------------------------------------------------- rules

    private TabPage BuildRulesTab()
    {
        var page = new TabPage("Rules") { Padding = new Padding(8) };

        _rulesList.View = View.Details;
        _rulesList.Dock = DockStyle.Fill;
        _rulesList.FullRowSelect = true;
        _rulesList.MultiSelect = false;
        _rulesList.HideSelection = false;
        _rulesList.GridLines = true;
        _rulesList.Columns.Add("Rule", 150);
        _rulesList.Columns.Add("State", 90);
        _rulesList.Columns.Add("Mode", 110);
        _rulesList.Columns.Add("Source", 220);
        _rulesList.Columns.Add("Target", 220);
        _rulesList.Columns.Add("Template", 180);
        _rulesList.Columns.Add("Pending", 70);
        _rulesList.Columns.Add("Detail", 320);
        _rulesList.DoubleClick += (_, _) => EditSelectedRule();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.Add(Button("Add rule...", AddRule));
        buttons.Controls.Add(Button("Edit...", EditSelectedRule));
        buttons.Controls.Add(Button("Duplicate", DuplicateRule));
        buttons.Controls.Add(Button("Remove", RemoveRule));
        buttons.Controls.Add(Button("Enable / disable", ToggleRuleEnabled));
        buttons.Controls.Add(Button("Preview (dry pass)...", PreviewSelectedRule));
        buttons.Controls.Add(Button("Clear symlinks...", ClearSymlinksForSelectedRule));

        page.Controls.Add(_rulesList);
        page.Controls.Add(buttons);
        return page;
    }

    // --------------------------------------------------------------- queue

    private TabPage BuildQueueTab()
    {
        var page = new TabPage("Queue") { Padding = new Padding(8) };

        _queueList.View = View.Details;
        _queueList.Dock = DockStyle.Fill;
        _queueList.FullRowSelect = true;
        _queueList.GridLines = true;
        _queueList.OwnerDraw = true;
        _queueList.Columns.Add("Rule", 110);
        _queueList.Columns.Add("File", 240);
        _queueList.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _queueList.Columns.Add("Modified", 130);
        _queueList.Columns.Add("State", 95);
        _queueList.Columns.Add("Progress", 120);
        _queueList.Columns.Add("Detail", 300);
        _queueList.Columns.Add("Target", 340);
        _queueList.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        // Must NOT set DrawDefault here: doing so tells the ListView it has drawn the whole row,
        // and DrawSubItem is then never raised, so the progress bar would silently never appear.
        _queueList.DrawItem += (_, _) => { };
        _queueList.DrawSubItem += DrawQueueSubItem;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(Button("Clear finished", () =>
        {
            foreach (var r in _service.Runners) r.ClearRecent();
            RefreshQueue();
        }));
        buttons.Controls.Add(Button("Open target folder", OpenSelectedTargetFolder));

        page.Controls.Add(_queueList);
        page.Controls.Add(buttons);
        return page;
    }

    /// <summary>Draws an inline bar in the Progress column; everything else uses the default.</summary>
    private void DrawQueueSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        const int progressColumn = 5;
        if (e.ColumnIndex != progressColumn || e.Item is null)
        {
            e.DrawDefault = true;
            return;
        }

        var item = e.Item.Tag as QueueItem;
        var bounds = e.Bounds;
        var selected = e.Item.Selected;

        using (var back = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Window))
            e.Graphics.FillRectangle(back, bounds);

        if (item is null || item.State is not (ItemState.Transferring or ItemState.Done)) return;

        var bar = Rectangle.Inflate(bounds, -4, -5);
        if (bar.Width <= 0 || bar.Height <= 0) return;

        e.Graphics.FillRectangle(Brushes.Gainsboro, bar);

        var fraction = item.State == ItemState.Done ? 1.0 : item.ProgressFraction;
        var filled = new Rectangle(bar.X, bar.Y, (int)(bar.Width * fraction), bar.Height);
        using (var brush = new SolidBrush(item.State == ItemState.Done
                   ? Color.FromArgb(0x2E, 0xA0, 0x43)
                   : Color.FromArgb(0x2F, 0x7A, 0xD1)))
            e.Graphics.FillRectangle(brush, filled);

        var caption = item.State == ItemState.Done ? "done" : $"{fraction * 100:F0}% {item.Phase}";
        TextRenderer.DrawText(e.Graphics, caption, Font, bounds, SystemColors.ControlText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // ----------------------------------------------------------------- log

    private TabPage BuildLogTab()
    {
        var page = new TabPage("Log") { Padding = new Padding(8) };

        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.BackColor = Color.White;
        _logBox.Font = new Font("Consolas", 9f);
        _logBox.WordWrap = false;
        _logBox.ScrollBars = RichTextBoxScrollBars.Both;
        _logBox.DetectUrls = false;

        _logLevelFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _logLevelFilter.Items.AddRange(new object[] { "Debug", "Info", "Warn", "Error" });
        _logLevelFilter.SelectedItem = "Info";
        _logLevelFilter.Width = 90;
        _logLevelFilter.SelectedIndexChanged += (_, _) => RerenderLog();

        _logSearch.Width = 220;
        _logSearch.PlaceholderText = "filter text...";
        _logSearch.TextChanged += (_, _) => RerenderLog();

        _autoScroll.Text = "Auto-scroll";
        _autoScroll.Checked = true;
        _autoScroll.AutoSize = true;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        bar.Controls.Add(new Label { Text = "Level", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        bar.Controls.Add(_logLevelFilter);
        bar.Controls.Add(_logSearch);
        bar.Controls.Add(_autoScroll);
        bar.Controls.Add(Button("Open log folder", () => OpenInExplorer(AppPaths.LogDirectory)));
        bar.Controls.Add(Button("Clear view", () => { _logBox.Clear(); }));

        page.Controls.Add(_logBox);
        page.Controls.Add(bar);
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Settings") { Padding = new Padding(8), AutoScroll = true };
        _settings = new SettingsPanel(_service, SaveConfig) { Dock = DockStyle.Fill };
        page.Controls.Add(_settings);
        return page;
    }

    // =============================================================== refresh

    private void Tick()
    {
        if (!Visible) { AppendNewLogEntries(); return; }
        RefreshQueue();
        RefreshRules();
        AppendNewLogEntries();
        _statusLabel.Text = _service.StatusLine;
        UpdateToolbar();
    }

    public void RefreshAll()
    {
        RefreshRules();
        RefreshQueue();
        _statusLabel.Text = _service.StatusLine;
        UpdateToolbar();
        _settings?.Reload();
    }

    private void UpdateToolbar()
    {
        _pauseButton.Text = _service.Paused ? "Resume" : "Pause";
        _dryRunButton.Checked = _service.Config.GlobalDryRun;
        _dryRunButton.Text = _service.Config.GlobalDryRun ? "Global dry run: ON" : "Global dry run: off";
    }

    private void RefreshRules()
    {
        var selectedId = SelectedRule()?.Id;

        _suspendUi = true;
        _rulesList.BeginUpdate();
        _rulesList.Items.Clear();

        foreach (var runner in _service.Runners)
        {
            var r = runner.Rule;
            var dry = _service.Config.GlobalDryRun || r.DryRun;
            var mode = $"{r.Mode}{(dry ? " (dry run)" : "")}{(r.CreateSymlink && r.Mode == TransferMode.Move ? " +link" : "")}";

            var state = !r.Enabled ? "Disabled" : runner.State.ToString();

            var item = new ListViewItem(new[]
            {
                r.Name, state, mode, r.SourceFolder, r.TargetFolder, r.TargetTemplate,
                runner.PendingCount.ToString(), runner.Fault
            })
            { Tag = r };

            item.ForeColor = runner.State switch
            {
                RuleState.Faulted => Color.Firebrick,
                RuleState.Paused => Color.DarkGoldenrod,
                _ => r.Enabled ? SystemColors.ControlText : Color.Gray
            };

            _rulesList.Items.Add(item);
            if (r.Id == selectedId) item.Selected = true;
        }

        _rulesList.EndUpdate();
        _suspendUi = false;
    }

    private void RefreshQueue()
    {
        var items = _service.SnapshotQueue()
            .OrderBy(i => StateRank(i.State))
            .ThenByDescending(i => i.LastWriteUtc)
            .Take(500)
            .ToList();

        _queueList.BeginUpdate();
        _queueList.Items.Clear();

        foreach (var q in items)
        {
            var row = new ListViewItem(new[]
            {
                q.RuleName,
                q.FileName,
                TransferEngine.FormatSize(q.Size),
                q.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                q.State.ToString(),
                "",
                q.Detail,
                q.Target ?? ""
            })
            { Tag = q };

            row.ForeColor = q.State switch
            {
                ItemState.Failed => Color.Firebrick,
                ItemState.Blocked => Color.DarkOrange,
                ItemState.Skipped => Color.DarkGoldenrod,
                ItemState.Done => Color.ForestGreen,
                ItemState.Transferring => Color.RoyalBlue,
                _ => SystemColors.ControlText
            };

            _queueList.Items.Add(row);
        }

        _queueList.EndUpdate();
    }

    private static int StateRank(ItemState s) => s switch
    {
        ItemState.Transferring => 0,
        ItemState.Ready => 1,
        ItemState.Waiting => 2,
        ItemState.Pending => 3,
        ItemState.Failed => 4,
        ItemState.Blocked => 5,
        ItemState.Skipped => 6,
        _ => 7
    };

    // ---------------------------------------------------------------- log view

    private LogLevel FilterLevel => Enum.TryParse<LogLevel>((string)_logLevelFilter.SelectedItem!, out var l) ? l : LogLevel.Info;

    private void AppendNewLogEntries()
    {
        var entries = _service.Log.GetSince(_logSeq);
        if (entries.Count == 0) return;
        _logSeq = entries[^1].Seq;

        if (_logBox.IsDisposed) return;
        foreach (var e in entries) AppendLogLine(e);
        if (_autoScroll.Checked) ScrollLogToEnd();
    }

    private void RerenderLog()
    {
        _logBox.Clear();
        foreach (var e in _service.Log.GetSince(0)) AppendLogLine(e);
        if (_autoScroll.Checked) ScrollLogToEnd();
    }

    private void AppendLogLine(LogEntry e)
    {
        if (e.Level < FilterLevel) return;
        var text = e.Format();
        if (_logSearch.Text.Length > 0 &&
            !text.Contains(_logSearch.Text, StringComparison.OrdinalIgnoreCase)) return;

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = e.Level switch
        {
            LogLevel.Error => Color.Firebrick,
            LogLevel.Warn => Color.DarkOrange,
            LogLevel.Debug => Color.Gray,
            _ => Color.Black
        };
        _logBox.AppendText(text + Environment.NewLine);
        _logBox.SelectionColor = _logBox.ForeColor;
    }

    private void ScrollLogToEnd()
    {
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    // =============================================================== rule actions

    private RuleConfig? SelectedRule() =>
        _rulesList.SelectedItems.Count > 0 ? _rulesList.SelectedItems[0].Tag as RuleConfig : null;

    private void AddRule()
    {
        var rule = new RuleConfig { Name = $"Rule {_service.Config.Rules.Count + 1}" };
        using var editor = new RuleEditorForm(rule, _service.Log);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        _service.Config.Rules.Add(editor.Result);
        ApplyConfigChange();
    }

    private void EditSelectedRule()
    {
        if (_suspendUi) return;
        var rule = SelectedRule();
        if (rule is null) return;

        using var editor = new RuleEditorForm(rule, _service.Log);
        if (editor.ShowDialog(this) != DialogResult.OK) return;

        var index = _service.Config.Rules.FindIndex(r => r.Id == rule.Id);
        if (index >= 0) _service.Config.Rules[index] = editor.Result;
        ApplyConfigChange();
    }

    private void DuplicateRule()
    {
        var rule = SelectedRule();
        if (rule is null) return;

        var copy = rule.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = rule.Name + " (copy)";
        copy.Enabled = false;
        copy.DryRun = true;
        _service.Config.Rules.Add(copy);
        ApplyConfigChange();
    }

    private void RemoveRule()
    {
        var rule = SelectedRule();
        if (rule is null) return;

        if (MessageBox.Show($"Remove the rule \"{rule.Name}\"?\n\nNo files are affected.",
                "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _service.Config.Rules.RemoveAll(r => r.Id == rule.Id);
        ApplyConfigChange();
    }

    private void ToggleRuleEnabled()
    {
        var rule = SelectedRule();
        if (rule is null) return;

        if (!rule.Enabled)
        {
            var errors = rule.Validate();
            if (errors.Count > 0)
            {
                MessageBox.Show("This rule cannot be enabled yet:\n\n  " + string.Join("\n  ", errors),
                    "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var dry = _service.Config.GlobalDryRun || rule.DryRun;
            if (!dry && rule.Mode == TransferMode.Move)
            {
                var answer = MessageBox.Show(
                    $"Enable \"{rule.Name}\" in MOVE mode, for real?\n\n" +
                    "Source files will be deleted, but only after the destination copy has been " +
                    "read back and verified byte for byte.\n\n" +
                    "If you have not watched this rule in dry run yet, do that first.",
                    "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }
        }

        rule.Enabled = !rule.Enabled;
        ApplyConfigChange();
    }

    private void PreviewSelectedRule()
    {
        var rule = SelectedRule();
        if (rule is null)
        {
            MessageBox.Show("Select a rule first.", "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var preview = new PreviewForm(rule);
        preview.ShowDialog(this);
    }

    private void ClearSymlinksForSelectedRule()
    {
        var rule = SelectedRule();
        if (rule is null)
        {
            MessageBox.Show("Select a rule first.", "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(rule.SourceFolder))
        {
            MessageBox.Show("This rule has no source folder set.", "MSmover",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var cleanup = new SymlinkCleanupForm(rule, _service.Log);
        cleanup.ShowDialog(this);
    }

    private void ApplyConfigChange()
    {
        SaveConfig();
        _service.Reload(_service.Config);
        RefreshAll();
    }

    private void ToggleGlobalDryRun()
    {
        if (_service.Config.GlobalDryRun)
        {
            var answer = MessageBox.Show(
                "Turn OFF global dry run?\n\nRules that are not individually in dry run will start " +
                "transferring files for real.",
                "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        _service.SetGlobalDryRun(!_service.Config.GlobalDryRun);
        SaveConfig();
        RefreshAll();
    }

    private void SaveConfig()
    {
        try { ConfigStore.Save(_service.Config); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the configuration:\n\n{ex.Message}",
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedTargetFolder()
    {
        if (_queueList.SelectedItems.Count == 0) return;
        if (_queueList.SelectedItems[0].Tag is not QueueItem q || string.IsNullOrEmpty(q.Target)) return;
        OpenInExplorer(Path.GetDirectoryName(q.Target)!);
    }

    internal static void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {path}:\n\n{ex.Message}", "MSmover",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static Button Button(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    // A second instance broadcasts this message instead of starting up.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Program.ShowWindowMessage && m.Msg != 0)
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
