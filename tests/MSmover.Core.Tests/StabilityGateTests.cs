using MSmover.Core.Detection;
using Xunit;

namespace MSmover.Core.Tests;

public class StabilityGateTests
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public void A_settled_file_is_ready()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("ready.raw", 10_000);
        var gate = new StabilityGate();

        var r = gate.Evaluate(path, ws.Rule(), Now);

        Assert.Equal(GateStatus.Ready, r.Status);
    }

    [Fact]
    public void A_file_younger_than_the_minimum_age_waits()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("young.raw", 10_000);
        var rule = ws.Rule(r => r.MinAgeSeconds = 3600);

        var r = new StabilityGate().Evaluate(path, rule, Now);

        Assert.Equal(GateStatus.Waiting, r.Status);
        Assert.Contains("too recent", r.Reason);
    }

    [Fact]
    public void A_file_below_the_minimum_size_waits()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("stub.raw", 10);
        var rule = ws.Rule(r => r.MinSizeBytes = 1024);

        var r = new StabilityGate().Evaluate(path, rule, Now);

        Assert.Equal(GateStatus.Waiting, r.Status);
        Assert.Contains("below minimum size", r.Reason);
    }

    [Fact]
    public void A_file_that_is_still_growing_never_reaches_ready()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("growing.raw", 1_000);
        var rule = ws.Rule(r => { r.StabilityProbes = 2; r.StabilityIntervalSeconds = 1; });
        var gate = new StabilityGate();
        var t0 = Now;

        Assert.Equal(GateStatus.Waiting, gate.Evaluate(path, rule, t0).Status);

        // It grew between probes: the counter must restart, not accumulate.
        File.AppendAllText(path, new string('x', 500));
        var second = gate.Evaluate(path, rule, t0.AddSeconds(2));
        Assert.Equal(GateStatus.Waiting, second.Status);
        Assert.Contains("1/2", second.Reason);

        // Unchanged now, so the second consecutive matching probe passes.
        Assert.Equal(GateStatus.Ready, gate.Evaluate(path, rule, t0.AddSeconds(4)).Status);
    }

    [Fact]
    public void Probes_closer_together_than_the_interval_do_not_count()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("paced.raw", 1_000);
        var rule = ws.Rule(r => { r.StabilityProbes = 3; r.StabilityIntervalSeconds = 10; });
        var gate = new StabilityGate();
        var t0 = Now;

        gate.Evaluate(path, rule, t0);
        var tooSoon = gate.Evaluate(path, rule, t0.AddSeconds(1));

        Assert.Equal(GateStatus.Waiting, tooSoon.Status);
        Assert.Contains("next probe in", tooSoon.Reason);
    }

    [Fact]
    public void A_missing_companion_file_holds_the_file_back()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("withsib.raw", 10_000);
        var rule = ws.Rule(r => r.RequireSiblingGlob = "{basename}.sld");

        var before = new StabilityGate().Evaluate(path, rule, Now);
        Assert.Equal(GateStatus.Waiting, before.Status);
        Assert.Contains("companion file", before.Reason);

        File.WriteAllText(Path.Combine(ws.Source, "withsib.sld"), "sequence");

        Assert.Equal(GateStatus.Ready, new StabilityGate().Evaluate(path, rule, Now).Status);
    }

    [Fact]
    public void A_vanished_file_is_dropped_rather_than_retried_forever()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Source, "gone.raw");

        var r = new StabilityGate().Evaluate(path, ws.Rule(), Now);

        Assert.Equal(GateStatus.Drop, r.Status);
    }

    /// <summary>
    /// The behaviour that matters most in the field. Thermo Xcalibur holds the .raw file open for
    /// the whole acquisition, so a file being actively written must never be reported ready --
    /// and note that Windows may not even update the directory entry's size or mtime until the
    /// handle is closed, which is exactly why the exclusive-open test is the primary signal.
    /// </summary>
    [Fact]
    public async Task A_file_being_actively_written_is_never_ready_until_the_handle_closes()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Source, "acquiring.raw");
        var rule = ws.Rule(r => { r.MinAgeSeconds = 0; r.StabilityProbes = 1; r.MinSizeBytes = 1; });
        var gate = new StabilityGate();

        var stop = new TaskCompletionSource();
        var writing = Task.Run(async () =>
        {
            // FileShare.None: exactly how an acquisition holds its output file.
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var chunk = new byte[64 * 1024];
            for (var i = 0; i < 40; i++)
            {
                await fs.WriteAsync(chunk);
                await fs.FlushAsync();
                await Task.Delay(15);
            }
            await stop.Task;
        });

        // While the writer holds the handle the gate must refuse, every time.
        for (var i = 0; i < 10; i++)
        {
            var r = gate.Evaluate(path, rule, Now);
            Assert.NotEqual(GateStatus.Ready, r.Status);
            await Task.Delay(30);
        }

        stop.SetResult();
        await writing;

        // Two probes because StabilityProbes counts consecutive readings.
        gate.Evaluate(path, rule, Now);
        var after = gate.Evaluate(path, rule, Now.AddSeconds(rule.StabilityIntervalSeconds + 1));

        Assert.Equal(GateStatus.Ready, after.Status);
    }
}
