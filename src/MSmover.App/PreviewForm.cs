using System.Text;
using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Naming;
using MSmover.Core.Transfer;

namespace MSmover.App;

/// <summary>
/// A dry pass over the current contents of the source folder: what would go where, and what
/// would be skipped and why. Read-only - it opens nothing for writing and creates nothing.
/// </summary>
public sealed class PreviewForm : Form
{
    private readonly RuleConfig _rule;
    private readonly ListView _list = new();
    private readonly Label _summary = new();
    private CancellationTokenSource? _cts;

    private sealed record Row(string File, long Size, DateTime Modified, string Verdict, string Detail, bool Good);

    public PreviewForm(RuleConfig rule)
    {
        _rule = rule;

        Text = $"Preview - {rule.Name}  ({rule.Mode}, {(rule.Recursive ? "recursive" : "top level only")})";
        Icon = TrayIcons.For(Core.Engine.ServiceHealth.Idle);
        Size = new Size(1100, 640);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);

        _list.View = View.Details;
        _list.Dock = DockStyle.Fill;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.Columns.Add("File", 260);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Modified", 130);
        _list.Columns.Add("Verdict", 150);
        _list.Columns.Add("Target, or why not", 440);

        _summary.Dock = DockStyle.Top;
        _summary.AutoSize = false;
        _summary.Height = 26;
        _summary.Padding = new Padding(4, 6, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(MainForm.Button("Refresh", () => _ = RunAsync()));
        buttons.Controls.Add(MainForm.Button("Copy to clipboard", CopyToClipboard));
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(14, 3, 14, 3) };
        buttons.Controls.Add(close);
        CancelButton = close;

        Controls.Add(_list);
        Controls.Add(_summary);
        Controls.Add(buttons);

        Shown += (_, _) => _ = RunAsync();
        FormClosed += (_, _) => _cts?.Cancel();
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _list.Items.Clear();
        _summary.Text = "Scanning...";
        Cursor = Cursors.WaitCursor;

        List<Row> rows;
        try
        {
            rows = await Task.Run(() => Scan(ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Cursor = Cursors.Default;
            _summary.Text = $"Scan failed: {ex.Message}";
            return;
        }
        finally { Cursor = Cursors.Default; }

        if (ct.IsCancellationRequested) return;

        _list.BeginUpdate();
        foreach (var r in rows)
        {
            var item = new ListViewItem(new[]
            {
                r.File,
                TransferEngine.FormatSize(r.Size),
                r.Modified.ToString("yyyy-MM-dd HH:mm"),
                r.Verdict,
                r.Detail
            });
            item.ForeColor = r.Good ? Color.FromArgb(0x1B, 0x6E, 0x30) : Color.Firebrick;
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        var would = rows.Count(r => r.Good);
        _summary.Text = $"{rows.Count} file(s) examined:  {would} would be {_rule.Mode.ToString().ToLowerInvariant()}d," +
                        $"  {rows.Count - would} would not.   Nothing was changed.";
    }

    private List<Row> Scan(CancellationToken ct)
    {
        var rows = new List<Row>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = _rule.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(LongPath.Prefix(_rule.SourceFolder), "*", options); }
        catch (Exception ex) { throw new InvalidOperationException($"cannot read {_rule.SourceFolder}: {ex.Message}"); }

        foreach (var raw in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = LongPath.Strip(raw);

            if (path.EndsWith(TransferEngine.PartSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.EndsWith(TransferEngine.LinkSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            FileInfo info;
            try
            {
                info = new FileInfo(LongPath.Prefix(path));
                if (!info.Exists) continue;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            }
            catch { continue; }

            var stamp = _rule.DateTokenSource == DateTokenSource.Now ? DateTime.Now : info.LastWriteTime;
            var map = PathMapper.Map(_rule, path, stamp);

            var display = Path.GetRelativePath(_rule.SourceFolder, path);

            if (map.Verdict == MapVerdict.NotIncluded) continue;          // not our business at all

            if (!map.Ok)
            {
                rows.Add(new Row(display, info.Length, info.LastWriteTime, "skip", map.Reason, false));
                continue;
            }

            if (info.Length < _rule.MinSizeBytes)
            {
                rows.Add(new Row(display, info.Length, info.LastWriteTime, "wait",
                    $"below the minimum size of {_rule.MinSizeBytes} bytes", false));
                continue;
            }

            var age = DateTime.UtcNow - info.LastWriteTimeUtc;
            if (age < TimeSpan.FromSeconds(_rule.MinAgeSeconds))
            {
                rows.Add(new Row(display, info.Length, info.LastWriteTime, "wait",
                    $"written {(int)age.TotalSeconds}s ago, minimum age is {_rule.MinAgeSeconds}s", false));
                continue;
            }

            if (!FileGuard.IsUnlocked(path))
            {
                rows.Add(new Row(display, info.Length, info.LastWriteTime, "wait",
                    "still open in another process - probably still being acquired", false));
                continue;
            }

            if (File.Exists(LongPath.Prefix(map.FullTarget!)))
            {
                rows.Add(new Row(display, info.Length, info.LastWriteTime, "skip",
                    $"a file already exists at {map.FullTarget}", false));
                continue;
            }

            var verb = _rule.Mode == TransferMode.Move ? "would move" : "would copy";
            if (_rule.Mode == TransferMode.Move && _rule.CreateSymlink) verb += " +link";
            rows.Add(new Row(display, info.Length, info.LastWriteTime, verb, map.FullTarget!, true));
        }

        return _rule.Order == QueueOrder.NewestFirst
            ? rows.OrderByDescending(r => r.Modified).ToList()
            : rows.OrderBy(r => r.Modified).ToList();
    }

    private void CopyToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine("file\tsize\tmodified\tverdict\tdetail");
        foreach (ListViewItem item in _list.Items)
            sb.AppendLine(string.Join('\t', item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)));

        try
        {
            Clipboard.SetText(sb.ToString());
            _summary.Text = $"Copied {_list.Items.Count} row(s) to the clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not copy to the clipboard: {ex.Message}", "MSmover",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
