using MSmover.Core.Common;
using MSmover.Core.Engine;
using MSmover.Core.Logging;

namespace MSmover.App;

public sealed class SettingsPanel : UserControl
{
    private readonly MoverService _service;
    private readonly Action _save;

    private readonly CheckBox _globalDryRun;
    private readonly CheckBox _autoStart;
    private readonly CheckBox _startMinimised;
    private readonly NumericUpDown _maxConcurrent;
    private readonly NumericUpDown _chunkKb;
    private readonly ComboBox _logLevel;
    private readonly NumericUpDown _logRetention;
    private readonly Label _symlinkStatus = new();

    private bool _ready;

    public SettingsPanel(MoverService service, Action save)
    {
        _service = service;
        _save = save;
        var c = service.Config;

        _globalDryRun = Ui.Check("Global dry run - no rule may write, copy or delete anything", c.GlobalDryRun);
        _autoStart = Ui.Check("Start MSmover automatically when I log in", AutoStart.IsEnabled(Program.ExecutablePath));
        _startMinimised = Ui.Check("Start minimised to the tray", c.StartMinimised);
        _maxConcurrent = Ui.Num(c.GlobalMaxConcurrentTransfers, 1, 16, 70);
        _chunkKb = Ui.Num(c.CopyChunkBytes / 1024, 64, 16384, 90);
        _logLevel = Ui.Combo(c.LogLevel);
        _logRetention = Ui.Num(c.LogRetentionDays, 1, 365, 70);

        AutoScroll = true;
        Padding = new Padding(12, 8, 12, 8);
        BuildLayout();
        _ready = true;
        RefreshSymlinkStatus();
    }

    private void BuildLayout()
    {
        var t = Ui.Grid();

        Ui.Section(t, "Safety");
        Ui.Full(t, _globalDryRun);
        Ui.Full(t, new Label
        {
            Text = "Deletions are never propagated. MSmover is one-way and per-file: it does not " +
                   "enumerate the target to reconcile it against the source, and contains no code path " +
                   "that deletes anything in the target folder. Mirror and sync semantics are " +
                   "deliberately not implemented.",
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            ForeColor = Color.Gray,
            Margin = new Padding(0, 2, 0, 8)
        });

        Ui.Section(t, "Startup");
        Ui.Full(t, _autoStart);
        Ui.Full(t, _startMinimised);

        Ui.Section(t, "Transfers");
        Ui.Row(t, "Max concurrent transfers", _maxConcurrent, "across all rules");
        Ui.Row(t, "Copy buffer", _chunkKb, "KB");

        Ui.Section(t, "Logging");
        Ui.Row(t, "Level", _logLevel);
        Ui.Row(t, "Keep log files for", _logRetention, "days");

        Ui.Section(t, "Symlink capability");
        _symlinkStatus.AutoSize = true;
        _symlinkStatus.MaximumSize = new Size(720, 0);
        _symlinkStatus.Font = new Font("Consolas", 9f);
        Ui.Full(t, _symlinkStatus);
        Ui.Full(t, FlowOf(
            MainForm.Button("Re-check", RefreshSymlinkStatus),
            MainForm.Button("Enable remote symlink following (admin)...", FixSymlinkEvaluation)));

        Ui.Section(t, "Files and folders");
        Ui.Full(t, FlowOf(
            MainForm.Button("Open settings folder", () => MainForm.OpenInExplorer(AppPaths.Root)),
            MainForm.Button("Open log folder", () => MainForm.OpenInExplorer(AppPaths.LogDirectory))));
        Ui.Full(t, new Label
        {
            Text = $"Configuration:  {AppPaths.ConfigFile}\nJournal:        {AppPaths.JournalFile}",
            AutoSize = true, ForeColor = Color.Gray, Font = new Font("Consolas", 8.5f),
            Margin = new Padding(0, 4, 0, 8)
        });

        Ui.Section(t, "");
        Ui.Full(t, FlowOf(MainForm.Button("Apply settings", Apply)));

        Controls.Add(t);
    }

    private static Control FlowOf(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
        f.Controls.AddRange(controls);
        return f;
    }

    public void Reload()
    {
        if (!_ready) return;
        var c = _service.Config;
        _globalDryRun.Checked = c.GlobalDryRun;
        _startMinimised.Checked = c.StartMinimised;
        _maxConcurrent.Value = Math.Clamp(c.GlobalMaxConcurrentTransfers, 1, 16);
        _chunkKb.Value = Math.Clamp(c.CopyChunkBytes / 1024, 64, 16384);
        _logLevel.SelectedItem = c.LogLevel;
        _logRetention.Value = Math.Clamp(c.LogRetentionDays, 1, 365);
    }

    private void Apply()
    {
        var c = _service.Config;

        if (!c.GlobalDryRun && !_globalDryRun.Checked)
        {
            // no change
        }
        else if (c.GlobalDryRun && !_globalDryRun.Checked)
        {
            var answer = MessageBox.Show(
                "Turn OFF global dry run?\n\nRules that are not individually in dry run will start " +
                "transferring files for real.",
                "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { _globalDryRun.Checked = true; return; }
        }

        c.GlobalDryRun = _globalDryRun.Checked;
        c.StartMinimised = _startMinimised.Checked;
        c.GlobalMaxConcurrentTransfers = (int)_maxConcurrent.Value;
        c.CopyChunkBytes = (int)_chunkKb.Value * 1024;
        c.LogLevel = (LogLevel)_logLevel.SelectedItem!;
        c.LogRetentionDays = (int)_logRetention.Value;

        try
        {
            AutoStart.Set(_autoStart.Checked, Program.ExecutablePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not change the autostart setting:\n\n{ex.Message}",
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _save();
        _service.Log.MinimumLevel = c.LogLevel;
        _service.Reload(c);
        MessageBox.Show("Settings applied.", "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RefreshSymlinkStatus()
    {
        var eval = Core.Transfer.SymlinkService.QueryEvaluation();
        var dev = Core.Transfer.SymlinkService.IsDeveloperModeEnabled();
        var elevated = Core.Transfer.SymlinkService.IsElevated();

        string Mark(bool b) => b ? "enabled" : "DISABLED";

        _symlinkStatus.Text =
            $"Developer Mode        : {(dev ? "on" : "off")}\n" +
            $"Running elevated      : {(elevated ? "yes" : "no")}\n" +
            $"Local  -> local  links: {Mark(eval.L2L)}\n" +
            $"Local  -> remote links: {Mark(eval.L2R)}   <- the one that matters for a network target\n" +
            $"Remote -> local  links: {Mark(eval.R2L)}\n" +
            $"Remote -> remote links: {Mark(eval.R2R)}";

        _symlinkStatus.ForeColor = (dev || elevated) && eval.L2R ? Color.FromArgb(0x1B, 0x6E, 0x30) : Color.Firebrick;
    }

    private void FixSymlinkEvaluation()
    {
        var answer = MessageBox.Show(
            "This runs the following command as administrator:\n\n" +
            $"    {Core.Transfer.SymlinkService.FixCommand}\n\n" +
            "It lets Windows follow symbolic links that point at network locations, which is what " +
            "makes the link left behind at the source usable.\n\nContinue?",
            "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + Core.Transfer.SymlinkService.FixCommand + " && pause",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not run the command:\n\n{ex.Message}",
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
