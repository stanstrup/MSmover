using MSmover.Core.Config;
using MSmover.Core.Logging;
using MSmover.Core.Naming;
using MSmover.Core.Transfer;

namespace MSmover.App;

public sealed class RuleEditorForm : Form
{
    private readonly RuleConfig _original;
    private readonly LogHub _log;

    // rule
    private readonly TextBox _name = new();
    private readonly CheckBox _enabled;
    private readonly CheckBox _dryRun;

    // folders
    private readonly TextBox _source = new();
    private readonly TextBox _target = new();
    private readonly CheckBox _recursive;
    private readonly ComboBox _mode;

    // which files
    private readonly TextBox _includeRegex = new();
    private readonly TextBox _excludeRegex = new();
    private readonly NumericUpDown _minSize;

    // completion
    private readonly NumericUpDown _minAge;
    private readonly NumericUpDown _probes;
    private readonly NumericUpDown _probeInterval;
    private readonly TextBox _sibling = new();

    // mapping
    private readonly TextBox _delimiter = new();
    private readonly CheckBox _checkDelims;
    private readonly NumericUpDown _expectedDelims;
    private readonly TextBox _template = new();
    private readonly ComboBox _dateSource;
    private readonly TextBox _testName = new();
    private readonly Label _preview = new();

    // safety
    private readonly ComboBox _verify;
    private readonly ComboBox _hash;
    private readonly CheckBox _symlink;
    private readonly NumericUpDown _maxRetries;
    private readonly NumericUpDown _backoff;

    // other
    private readonly ComboBox _order;
    private readonly NumericUpDown _parallelism;
    private readonly NumericUpDown _rescan;
    private readonly CheckBox _pruneDirs;
    private readonly TextBox _indexFile = new();
    private readonly TextBox _externalCommand = new();

    private bool _ready;

    public RuleConfig Result { get; private set; } = null!;

    public RuleEditorForm(RuleConfig rule, LogHub log)
    {
        _original = rule;
        _log = log;

        _enabled = Ui.Check("Enabled", rule.Enabled);
        _dryRun = Ui.Check("Dry run (report only, change nothing)", rule.DryRun);
        _recursive = Ui.Check("Include sub-folders", rule.Recursive);
        _mode = Ui.Combo(rule.Mode);
        _minSize = Ui.Num(rule.MinSizeBytes, 0, 1_000_000_000_000m, 130);
        _minAge = Ui.Num(rule.MinAgeSeconds, 0, 86400);
        _probes = Ui.Num(rule.StabilityProbes, 1, 20);
        _probeInterval = Ui.Num(rule.StabilityIntervalSeconds, 1, 3600);
        _checkDelims = Ui.Check("Require exactly", rule.ExpectedDelimiterCount is not null);
        _expectedDelims = Ui.Num(rule.ExpectedDelimiterCount ?? 2, 0, 20, 60);
        _dateSource = Ui.Combo(rule.DateTokenSource);
        _verify = Ui.Combo(rule.VerifyMode);
        _hash = Ui.Combo(rule.HashAlgorithm);
        _symlink = Ui.Check("Leave a symbolic link at the original location (move mode only)", rule.CreateSymlink);
        _maxRetries = Ui.Num(rule.MaxRetries, 0, 100);
        _backoff = Ui.Num(rule.RetryBackoffSeconds, 1, 3600);
        _order = Ui.Combo(rule.Order);
        _parallelism = Ui.Num(rule.Parallelism, 1, 8, 60);
        _rescan = Ui.Num(rule.RescanSeconds, 15, 86400);
        _pruneDirs = Ui.Check("Delete source sub-folders once they are empty", rule.DeleteEmptySourceDirs);

        _name.Text = rule.Name;
        _source.Text = rule.SourceFolder;
        _target.Text = rule.TargetFolder;
        _includeRegex.Text = rule.IncludeRegex;
        _excludeRegex.Text = rule.ExcludeRegex;
        _sibling.Text = rule.RequireSiblingGlob;
        _delimiter.Text = rule.Delimiter;
        _template.Text = rule.TargetTemplate;
        _indexFile.Text = rule.IndexFile;
        _externalCommand.Text = rule.ExternalCommand;
        _testName.Text = "MSTEST_A01_003.raw";

        Text = $"Rule - {rule.Name}";
        Icon = TrayIcons.For(Core.Engine.ServiceHealth.Idle);
        Size = new Size(880, 780);
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        _ready = true;
        UpdatePreview();
    }

    private void BuildLayout()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 8, 12, 8) };
        var t = Ui.Grid();

        Ui.Section(t, "Rule");
        Ui.Row(t, "Name", _name);
        Ui.Full(t, Flow(_enabled, _dryRun));

        Ui.Section(t, "Folders");
        Ui.Row(t, "Source folder", WithBrowse(_source, "Choose the instrument's acquisition folder"));
        Ui.Row(t, "Target folder", WithBrowse(_target, "Choose the network destination folder"));
        Ui.Full(t, Flow(_recursive));
        Ui.Row(t, "Mode", _mode, "Move deletes the source only after verification");

        Ui.Section(t, "Which files");
        Ui.Row(t, "Include regex", _includeRegex, "matched against the file name");
        Ui.Row(t, "Exclude regex", _excludeRegex, "leave empty for none");
        Ui.Row(t, "Minimum size", _minSize, "bytes - skips stubs");

        Ui.Section(t, "When is a file finished");
        Ui.Row(t, "Minimum age", _minAge, "seconds since last write");
        Ui.Row(t, "Stability probes", _probes, "consecutive readings with an unchanged size");
        Ui.Row(t, "Probe interval", _probeInterval, "seconds between readings");
        Ui.Row(t, "Required companion file", _sibling, "e.g. {basename}.sld - optional");
        Ui.Full(t, Note("A file must also be openable with no sharing, which is the strongest signal: " +
                        "an acquisition holds its .raw file open for the whole run."));

        Ui.Section(t, "Where it goes");
        Ui.Row(t, "Delimiter", _delimiter, "splits the name into {t1}, {t2}, ...");
        Ui.Row(t, "Delimiter count", Flow(_checkDelims, _expectedDelims,
            new Label { Text = "delimiter(s), else skip the file", AutoSize = true, Margin = new Padding(6, 7, 0, 0) }));
        Ui.Row(t, "Target template", _template);
        Ui.Row(t, "Date tokens from", _dateSource);
        Ui.Row(t, "Test with file name", _testName);

        _preview.AutoSize = false;
        _preview.Height = 54;
        _preview.Dock = DockStyle.Fill;
        _preview.Font = new Font("Consolas", 9f);
        _preview.Padding = new Padding(8, 6, 6, 6);
        _preview.BackColor = Color.FromArgb(0xF5, 0xF6, 0xF8);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        Ui.Row(t, "Resolves to", _preview);

        Ui.Full(t, Note(string.Join("   ", PathMapper.TokenHelp.Select(x => x.Token)) +
                        "\nHover-free reference: {t1}..{tN} split on the delimiter, {relpath} is the source sub-folder, " +
                        "{MM} is month and {mm} is minute."));

        Ui.Section(t, "Safety");
        Ui.Row(t, "Verification", _verify, "Hash: read the destination back and compare");
        Ui.Row(t, "Hash algorithm", _hash, "xxHash64 is fast and fine for integrity");
        Ui.Row(t, "If the target exists", new Label
        {
            Text = "Skip, leave the source untouched, and warn.",
            AutoSize = true, Margin = new Padding(0, 6, 0, 3)
        });
        Ui.Full(t, Flow(_symlink, MainForm.Button("Test symlink support...", TestSymlink)));
        Ui.Row(t, "Max retries", _maxRetries);
        Ui.Row(t, "Retry backoff", _backoff, "seconds, multiplied by the attempt number");

        Ui.Section(t, "Other");
        Ui.Row(t, "Order", _order);
        Ui.Row(t, "Parallel transfers", _parallelism, "also capped globally in Settings");
        Ui.Row(t, "Full rescan every", _rescan, "seconds - the fallback if the watcher misses an event");
        Ui.Full(t, Flow(_pruneDirs));
        Ui.Row(t, "Index file", _indexFile, "TSV at the target root - optional");
        Ui.Row(t, "External copy command", _externalCommand, "{src} {dst} - optional, verification still runs");

        scroll.Controls.Add(t);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, AutoSize = true, Padding = new Padding(14, 3, 14, 3) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 3, 14, 3) };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 8, 12, 8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(scroll);
        Controls.Add(buttons);
        CancelButton = cancel;

        foreach (var c in new Control[] { _delimiter, _template, _includeRegex, _excludeRegex, _testName, _source, _target })
            c.TextChanged += (_, _) => UpdatePreview();
        _checkDelims.CheckedChanged += (_, _) => UpdatePreview();
        _expectedDelims.ValueChanged += (_, _) => UpdatePreview();
        _dateSource.SelectedIndexChanged += (_, _) => UpdatePreview();
        _mode.SelectedIndexChanged += (_, _) => UpdatePreview();
    }

    private static Control Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        f.Controls.AddRange(controls);
        return f;
    }

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        ForeColor = Color.Gray,
        Margin = new Padding(0, 4, 0, 8)
    };

    private Control WithBrowse(TextBox box, string description)
    {
        var host = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0)
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        box.Margin = new Padding(0, 3, 6, 3);
        host.Controls.Add(box);
        host.Controls.Add(MainForm.Button("Browse...", () =>
        {
            var picked = Ui.BrowseFolder(box.Text, description);
            if (picked is not null) box.Text = picked;
        }));
        return host;
    }

    // ---------------------------------------------------------------- preview

    private RuleConfig Build()
    {
        var r = _original.Clone();
        r.Name = _name.Text.Trim();
        r.Enabled = _enabled.Checked;
        r.DryRun = _dryRun.Checked;
        r.SourceFolder = _source.Text.Trim();
        r.TargetFolder = _target.Text.Trim();
        r.Recursive = _recursive.Checked;
        r.Mode = (TransferMode)_mode.SelectedItem!;
        r.IncludeRegex = _includeRegex.Text;
        r.ExcludeRegex = _excludeRegex.Text;
        r.MinSizeBytes = (long)_minSize.Value;
        r.MinAgeSeconds = (int)_minAge.Value;
        r.StabilityProbes = (int)_probes.Value;
        r.StabilityIntervalSeconds = (int)_probeInterval.Value;
        r.RequireSiblingGlob = _sibling.Text.Trim();
        r.Delimiter = _delimiter.Text;
        r.ExpectedDelimiterCount = _checkDelims.Checked ? (int)_expectedDelims.Value : null;
        r.TargetTemplate = _template.Text.Trim();
        r.DateTokenSource = (DateTokenSource)_dateSource.SelectedItem!;
        r.VerifyMode = (VerifyMode)_verify.SelectedItem!;
        r.HashAlgorithm = (HashKind)_hash.SelectedItem!;
        r.CreateSymlink = _symlink.Checked;
        r.MaxRetries = (int)_maxRetries.Value;
        r.RetryBackoffSeconds = (int)_backoff.Value;
        r.Order = (QueueOrder)_order.SelectedItem!;
        r.Parallelism = (int)_parallelism.Value;
        r.RescanSeconds = (int)_rescan.Value;
        r.DeleteEmptySourceDirs = _pruneDirs.Checked;
        r.IndexFile = _indexFile.Text.Trim();
        r.ExternalCommand = _externalCommand.Text.Trim();
        return r;
    }

    private void UpdatePreview()
    {
        if (!_ready) return;
        _expectedDelims.Enabled = _checkDelims.Checked;
        _symlink.Enabled = (TransferMode)_mode.SelectedItem! == TransferMode.Move;

        var rule = Build();
        var name = _testName.Text.Trim();
        if (name.Length == 0)
        {
            _preview.ForeColor = Color.Gray;
            _preview.Text = "Type a file name above to see where it would go.";
            return;
        }

        var source = Path.Combine(
            string.IsNullOrWhiteSpace(rule.SourceFolder) ? @"C:\source" : rule.SourceFolder, name);

        var result = PathMapper.Map(rule, source, DateTime.Now);
        if (result.Ok)
        {
            _preview.ForeColor = Color.FromArgb(0x1B, 0x6E, 0x30);
            _preview.Text = result.FullTarget;
        }
        else
        {
            _preview.ForeColor = Color.Firebrick;
            _preview.Text = $"{result.Verdict}: {result.Reason}";
        }
    }

    private void TestSymlink()
    {
        var rule = Build();
        if (string.IsNullOrWhiteSpace(rule.SourceFolder) || string.IsNullOrWhiteSpace(rule.TargetFolder))
        {
            MessageBox.Show("Set the source and target folders first.", "MSmover",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Cursor = Cursors.WaitCursor;
        SymlinkCapability cap;
        try { cap = SymlinkService.Probe(rule.SourceFolder, rule.TargetFolder); }
        finally { Cursor = Cursors.Default; }

        if (cap.Usable)
        {
            MessageBox.Show(
                "Symlinks work here.\n\nA real link was created in the source folder pointing at the " +
                "target folder, verified, and removed again.",
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var explanation = SymlinkService.ExplainFailure(cap);
        var offerFix = cap.WillNotBeFollowed;

        if (!offerFix)
        {
            MessageBox.Show(explanation, "MSmover - symlinks are not available",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(
            explanation + "\n\nRun that command now? Windows will ask for administrator approval.",
            "MSmover - symlinks will not be followed",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;

        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + SymlinkService.FixCommand + " && pause",
                UseShellExecute = true,
                Verb = "runas"
            });
            _log.Info($"Ran elevated: {SymlinkService.FixCommand}", rule.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not run the command:\n\n{ex.Message}\n\nRun it yourself from an " +
                            $"elevated command prompt:\n\n    {SymlinkService.FixCommand}",
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Accept()
    {
        var rule = Build();
        var errors = rule.Validate();

        if (rule.Enabled && errors.Count > 0)
        {
            MessageBox.Show("This rule cannot be enabled yet:\n\n  " + string.Join("\n  ", errors),
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (errors.Count > 0)
        {
            var answer = MessageBox.Show(
                "The rule has problems and will not start until they are fixed:\n\n  " +
                string.Join("\n  ", errors) + "\n\nSave it anyway (disabled)?",
                "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            rule.Enabled = false;
        }

        if (rule.Enabled && !rule.DryRun && rule.Mode == TransferMode.Move)
        {
            var answer = MessageBox.Show(
                "This rule is enabled in MOVE mode with dry run off.\n\n" +
                "Source files will be deleted, but only after the destination has been read back " +
                "and verified byte for byte.\n\nContinue?",
                "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        Result = rule;
        DialogResult = DialogResult.OK;
        Close();
    }
}
