using System.Runtime.InteropServices;
using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Engine;
using MSmover.Core.Logging;

namespace MSmover.App;

internal static class Program
{
    /// <summary>Broadcast by a second instance to bring the running one to the front.</summary>
    internal static readonly int ShowWindowMessage =
        RegisterWindowMessage("MSmover.ShowMainWindow.7C1B0E4A");

    /// <summary>
    /// Assembly.Location is empty in a single-file publish, so ProcessPath is the only reliable
    /// answer; the AppContext fallback exists purely so this can never return null.
    /// </summary>
    internal static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MSmover.exe");

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\MSmover.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // Already running: ask that instance to show itself, then get out of the way.
            PostMessage(HWND_BROADCAST, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        AppPaths.EnsureCreated();

        AppConfig config;
        LogHub log;
        try
        {
            config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The configuration file could not be read:\n\n{AppPaths.ConfigFile}\n\n{ex.Message}\n\n" +
                "MSmover will start with no rules. Your existing file has not been changed - fix or " +
                "remove it, then restart.",
                "MSmover", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            config = new AppConfig();
        }

        log = new LogHub(capacity: 20_000, retentionDays: Math.Max(1, config.LogRetentionDays))
        {
            MinimumLevel = config.LogLevel
        };

        var service = new MoverService(config, log);

        Application.ThreadException += (_, e) =>
        {
            log.Error($"Unhandled UI exception: {e.Exception}");
            MessageBox.Show(e.Exception.ToString(), "MSmover - unexpected error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error($"Unhandled exception: {e.ExceptionObject}");

        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                          || config.StartMinimised;

        using var context = new TrayContext(service, startHidden);
        service.Start();
        Application.Run(context);
        service.Dispose();
        log.Dispose();

        GC.KeepAlive(mutex);
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xffff);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
