using System.Windows.Forms;
using MSmover.App;
using MSmover.Core.Config;
using MSmover.Core.Engine;
using MSmover.Core.Logging;
using Xunit;

namespace MSmover.Core.Tests;

/// <summary>
/// Constructs each window and forces its handle to be created, without ever showing it.
///
/// These exist because form construction is where a whole class of bug lives that no amount of
/// engine testing catches: a control property assigned in the wrong order, a null layout parent, a
/// missing resource. One of those (NumericUpDown validating Value against the default 0..100 range
/// before Maximum had been widened) took the entire application down on startup and was invisible
/// to every other test.
/// </summary>
public class FormSmokeTests
{
    /// <summary>WinForms requires a single-threaded apartment; xUnit gives us an MTA thread.</summary>
    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the form thread did not finish in time");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Realise(Form form)
    {
        // Forces the full control tree to be built, which is where construction bugs surface.
        _ = form.Handle;
        form.CreateControl();
        form.Dispose();
    }

    private static (AppConfig Config, LogHub Log, MoverService Service) NewService(TempWorkspace ws)
    {
        var config = new AppConfig();
        config.Rules.Add(SampleRule(ws));
        var log = new LogHub(capacity: 100, retentionDays: 1);
        return (config, log, new MoverService(config, log));
    }

    private static RuleConfig SampleRule(TempWorkspace ws) => new()
    {
        Name = "smoke",
        SourceFolder = ws.Source,
        TargetFolder = ws.Target,
        // Deliberately non-default so nothing is exercised only at its default value.
        Delimiter = "_",
        ExpectedDelimiterCount = 2,
        TargetTemplate = @"{t1}\{t1}.pro\Data\{filename}",
        MinSizeBytes = 4096,
        MinAgeSeconds = 120,
        RescanSeconds = 600,
        MaxRetries = 9,
        Parallelism = 4,
        HashAlgorithm = HashKind.Sha256,
        Mode = TransferMode.Move,
        CreateSymlink = true,
        IndexFile = "msmover_index.tsv"
    };

    [Fact]
    public void MainForm_constructs()
    {
        using var ws = new TempWorkspace();
        OnStaThread(() =>
        {
            var (_, _, service) = NewService(ws);
            using (service) Realise(new MainForm(service));
        });
    }

    [Fact]
    public void RuleEditorForm_constructs_and_previews()
    {
        using var ws = new TempWorkspace();
        OnStaThread(() =>
        {
            var log = new LogHub(capacity: 100, retentionDays: 1);
            using (log) Realise(new RuleEditorForm(SampleRule(ws), log));
        });
    }

    [Fact]
    public void PreviewForm_constructs()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("SMOKE_A01_001.raw", 8_000);
        OnStaThread(() => Realise(new PreviewForm(SampleRule(ws))));
    }

    [Fact]
    public void SymlinkCleanupForm_constructs()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("SMOKE_A01_002.raw", 8_000);
        OnStaThread(() =>
        {
            var log = new LogHub(capacity: 100, retentionDays: 1);
            using (log) Realise(new SymlinkCleanupForm(SampleRule(ws), log));
        });
    }

    [Fact]
    public void SettingsPanel_constructs_with_non_default_values()
    {
        using var ws = new TempWorkspace();
        OnStaThread(() =>
        {
            var (config, _, service) = NewService(ws);
            // The exact shape that crashed on startup: values well outside a control's default range.
            config.CopyChunkBytes = 8 * 1024 * 1024;
            config.GlobalMaxConcurrentTransfers = 9;
            config.LogRetentionDays = 200;

            using (service)
            {
                using var host = new Form();
                var panel = new SettingsPanel(service, () => { });
                host.Controls.Add(panel);
                Realise(host);
            }
        });
    }

    [Theory]
    [InlineData(ServiceHealth.Idle)]
    [InlineData(ServiceHealth.Working)]
    [InlineData(ServiceHealth.Paused)]
    [InlineData(ServiceHealth.Error)]
    public void Tray_icons_render_at_every_size_used(ServiceHealth health)
    {
        OnStaThread(() =>
        {
            Assert.NotNull(TrayIcons.For(health));
            foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
            {
                using var bitmap = TrayIcons.Render(TrayIcons.ColorFor(health), size);
                Assert.Equal(size, bitmap.Width);
                Assert.Equal(size, bitmap.Height);
            }
        });
    }
}
