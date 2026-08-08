using MSmover.Core.Config;
using MSmover.Core.Engine;

namespace MSmover.App;

/// <summary>
/// Owns the tray icon and the (hideable) main window. Closing the window hides it; only
/// Quit from the tray menu actually exits, which is what you want for something that must
/// keep running unattended.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly MoverService _service;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private MainForm? _form;
    private ServiceHealth _lastHealth = (ServiceHealth)(-1);

    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _dryRunItem;

    public TrayContext(MoverService service, bool startHidden)
    {
        _service = service;

        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _dryRunItem = new ToolStripMenuItem("Global dry run", null, (_, _) => ToggleDryRun())
        {
            CheckOnClick = false
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open MSmover", null, (_, _) => ShowMainWindow()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_dryRunItem);
        menu.Items.Add(new ToolStripMenuItem("Scan now", null, (_, _) => _service.ScanNow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.For(ServiceHealth.Idle),
            Text = "MSmover",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += (_, _) => RefreshTray();
        _timer.Start();

        // Created eagerly even when starting hidden: its window handle is what receives the
        // broadcast a second instance sends to bring this one to the front.
        EnsureForm();
        if (!startHidden) ShowMainWindow();
    }

    private void EnsureForm()
    {
        if (_form is not null && !_form.IsDisposed) return;

        _form = new MainForm(_service);
        _form.FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            _form!.Hide();
        };
        // Realise the handle without showing anything, so WndProc is live from the start.
        _ = _form.Handle;
    }

    public void ShowMainWindow()
    {
        EnsureForm();
        _form!.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    private void TogglePause()
    {
        _service.SetPaused(!_service.Paused);
        SaveConfigQuietly();
        RefreshTray();
        _form?.RefreshAll();
    }

    private void ToggleDryRun()
    {
        var turningOff = _service.Config.GlobalDryRun;
        if (turningOff)
        {
            var answer = MessageBox.Show(
                "Turn OFF global dry run?\n\nRules that are not individually in dry run will start " +
                "transferring files for real.",
                "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        _service.SetGlobalDryRun(!_service.Config.GlobalDryRun);
        SaveConfigQuietly();
        RefreshTray();
        _form?.RefreshAll();
    }

    private void SaveConfigQuietly()
    {
        try { ConfigStore.Save(_service.Config); }
        catch (Exception ex) { _service.Log.Warn($"Could not save configuration: {ex.Message}"); }
    }

    private void RefreshTray()
    {
        var health = _service.Health;
        if (health != _lastHealth)
        {
            _tray.Icon = TrayIcons.For(health);
            _lastHealth = health;
        }

        var text = "MSmover - " + _service.StatusLine;
        _tray.Text = text.Length > 63 ? text[..60] + "..." : text;   // Win32 tooltip limit

        _pauseItem.Text = _service.Paused ? "Resume" : "Pause";
        _dryRunItem.Checked = _service.Config.GlobalDryRun;
    }

    private void Quit()
    {
        var active = _service.Runners.Sum(r => r.InFlightCount);
        if (active > 0)
        {
            var answer = MessageBox.Show(
                $"{active} transfer(s) are in progress.\n\nQuitting now cancels them. No data is lost: " +
                "an incomplete destination file is discarded and the source is left untouched.\n\nQuit anyway?",
                "MSmover", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        _timer.Stop();
        _tray.Visible = false;
        _service.Stop();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            _form?.Dispose();
        }
        base.Dispose(disposing);
    }
}
