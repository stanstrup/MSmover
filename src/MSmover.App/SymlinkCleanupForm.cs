using MSmover.Core.Config;
using MSmover.Core.Logging;
using MSmover.Core.Transfer;

namespace MSmover.App;

/// <summary>
/// Lists the symbolic links under a rule's source folder and lets you remove them.
///
/// Deliberately a two-step tool: it shows you what it found and what each link points at before
/// anything is deleted. Removing a link never touches the file it points at.
/// </summary>
public sealed class SymlinkCleanupForm : Form
{
    private readonly RuleConfig _rule;
    private readonly LogHub _log;

    private readonly ListView _list = new();
    private readonly CheckBox _onlyIntoTarget;
    private readonly CheckBox _onlyBroken;
    private readonly CheckBox _recursive;
    private readonly Label _summary = new();
    private readonly Button _delete;

    private IReadOnlyList<SymlinkEntry> _found = Array.Empty<SymlinkEntry>();

    public SymlinkCleanupForm(RuleConfig rule, LogHub log)
    {
        _rule = rule;
        _log = log;

        _onlyIntoTarget = Ui.Check("Only links pointing into this rule's target folder", true);
        _onlyBroken = Ui.Check("Only broken links (target missing)", false);
        _recursive = Ui.Check("Include sub-folders", rule.Recursive);

        Text = $"Clear symbolic links - {rule.Name}";
        Icon = TrayIcons.For(Core.Engine.ServiceHealth.Idle);
        Size = new Size(1020, 600);
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);

        _delete = MainForm.Button("Delete selected links", DeleteSelected);

        BuildLayout();
        Shown += (_, _) => Rescan();
    }

    private void BuildLayout()
    {
        _list.View = View.Details;
        _list.Dock = DockStyle.Fill;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.CheckBoxes = true;
        _list.Columns.Add("Link in the source folder", 300);
        _list.Columns.Add("Kind", 70);
        _list.Columns.Add("Points at", 560);

        var header = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 52,
            Padding = new Padding(4, 6, 4, 0),
            Text = $"Source folder:  {_rule.SourceFolder}\n" +
                   "Deleting a link removes only the link. The file it points at is not touched."
        };

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        filters.Controls.Add(_onlyIntoTarget);
        filters.Controls.Add(_onlyBroken);
        filters.Controls.Add(_recursive);
        foreach (var c in new[] { _onlyIntoTarget, _onlyBroken, _recursive })
            c.CheckedChanged += (_, _) => Rescan();

        _summary.Dock = DockStyle.Bottom;
        _summary.AutoSize = false;
        _summary.Height = 24;
        _summary.Padding = new Padding(4, 4, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4, 6, 4, 6) };
        buttons.Controls.Add(MainForm.Button("Rescan", Rescan));
        buttons.Controls.Add(MainForm.Button("Select all", () => SetAllChecked(true)));
        buttons.Controls.Add(MainForm.Button("Select none", () => SetAllChecked(false)));
        buttons.Controls.Add(_delete);
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 3, 14, 3) };
        buttons.Controls.Add(close);
        CancelButton = close;

        Controls.Add(_list);
        Controls.Add(filters);
        Controls.Add(header);
        Controls.Add(_summary);
        Controls.Add(buttons);
    }

    private void Rescan()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            _found = SymlinkCleaner.Find(_rule.SourceFolder, _rule.TargetFolder, _recursive.Checked);
        }
        catch (Exception ex)
        {
            _summary.Text = $"Scan failed: {ex.Message}";
            return;
        }
        finally { Cursor = Cursors.Default; }

        var shown = _found
            .Where(e => !_onlyIntoTarget.Checked || e.PointsIntoRuleTarget)
            .Where(e => !_onlyBroken.Checked || !e.TargetExists)
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in shown)
        {
            var relative = e.LinkPath;
            try { relative = Path.GetRelativePath(_rule.SourceFolder, e.LinkPath); } catch { /* keep absolute */ }

            var item = new ListViewItem(new[] { relative, e.IsDirectory ? "folder" : "file", e.Describe })
            {
                Tag = e,
                Checked = true,
                ForeColor = e.TargetExists ? SystemColors.ControlText : Color.Firebrick
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        var hidden = _found.Count - shown.Count;
        _summary.Text = shown.Count == 0
            ? $"No symbolic links found in the source folder.{(hidden > 0 ? $"  ({hidden} hidden by the filters above.)" : "")}"
            : $"{shown.Count} link(s) listed{(hidden > 0 ? $", {hidden} hidden by the filters" : "")}. " +
              $"{shown.Count(e => !e.TargetExists)} broken.";

        _delete.Enabled = shown.Count > 0;
    }

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in _list.Items) item.Checked = value;
    }

    private void DeleteSelected()
    {
        var chosen = _list.Items.Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => (SymlinkEntry)i.Tag!)
            .ToList();

        if (chosen.Count == 0)
        {
            MessageBox.Show("Nothing is ticked.", "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var broken = chosen.Count(e => !e.TargetExists);
        var answer = MessageBox.Show(
            $"Delete {chosen.Count} symbolic link(s) from:\n\n{_rule.SourceFolder}\n\n" +
            (broken > 0 ? $"{broken} of them are broken (their target is missing).\n\n" : "") +
            "Only the links are removed. The files they point at are not touched.\n\nContinue?",
            "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        Cursor = Cursors.WaitCursor;
        SymlinkCleaner.CleanupResult result;
        try { result = SymlinkCleaner.Delete(chosen); }
        finally { Cursor = Cursors.Default; }

        _log.Info($"Removed {result.Deleted} symbolic link(s) from {_rule.SourceFolder}.", _rule.Name);
        foreach (var error in result.Errors) _log.Warn("Symlink cleanup: " + error, _rule.Name);

        if (result.Errors.Count > 0)
            MessageBox.Show(
                $"Deleted {result.Deleted} link(s).\n\n{result.Errors.Count} could not be removed:\n\n  " +
                string.Join("\n  ", result.Errors.Take(10)) +
                (result.Errors.Count > 10 ? "\n  ..." : ""),
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        Rescan();
    }
}
