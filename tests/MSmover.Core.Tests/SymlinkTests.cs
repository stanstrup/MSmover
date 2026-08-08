using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Transfer;
using Xunit;

namespace MSmover.Core.Tests;

/// <summary>
/// Symlink creation needs SeCreateSymbolicLinkPrivilege, which a normal build agent does not
/// have. These tests assert the probe reports the situation coherently either way, and exercise
/// the real move+link path only when the machine can actually do it.
/// </summary>
public class SymlinkTests
{
    [Fact]
    public void The_probe_reports_a_coherent_capability_and_leaves_nothing_behind()
    {
        using var ws = new TempWorkspace();

        var cap = SymlinkService.Probe(ws.Source, ws.Target);

        Assert.Equal(cap.CanCreate && cap.CanFollow, cap.Usable);
        if (!cap.Usable) Assert.False(string.IsNullOrWhiteSpace(SymlinkService.ExplainFailure(cap)));

        // Whatever the outcome, the probe must not litter either folder.
        Assert.Empty(Directory.EnumerateFileSystemEntries(ws.Source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(ws.Target));
    }

    [Fact]
    public void Fsutil_evaluation_parses_into_something_usable()
    {
        var eval = SymlinkService.QueryEvaluation();

        // Local-to-local is on by default on every supported Windows; if this comes back false the
        // output format has changed and the parser needs revisiting.
        Assert.True(eval.L2L, "Local-to-local symlink evaluation parsed as disabled, which means " +
                              "the fsutil output format was not understood.");
    }

    [Fact]
    public async Task Move_with_a_symlink_leaves_a_working_link_at_the_source()
    {
        using var ws = new TempWorkspace();

        var capability = SymlinkService.Probe(ws.Source, ws.Target);
        if (!capability.Usable)
            return;   // no privilege on this machine; the pre-flight path is what protects users

        var src = ws.WriteFile("LINKED_A01_001.raw", 120_000);
        var expected = File.ReadAllBytes(src);
        var dst = Path.Combine(ws.Target, "LINKED_A01_001.raw");

        var rule = ws.Rule(r =>
        {
            r.Mode = TransferMode.Move;
            r.CreateSymlink = true;
        });

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Transferred, result.Status);
        Assert.True(File.Exists(dst));
        Assert.True(File.Exists(src));                       // the link now occupies the old path
        Assert.True(FileGuard.IsReparsePoint(src));
        Assert.Equal(expected, File.ReadAllBytes(src));      // reads through to the target
        Assert.False(File.Exists(src + TransferEngine.LinkSuffix));
    }

    [Fact]
    public async Task A_symlink_left_by_a_previous_move_is_never_reprocessed()
    {
        using var ws = new TempWorkspace();

        var capability = SymlinkService.Probe(ws.Source, ws.Target);
        if (!capability.Usable) return;

        var src = ws.WriteFile("LINKED_A01_002.raw", 20_000);
        var dst = Path.Combine(ws.Target, "LINKED_A01_002.raw");
        var rule = ws.Rule(r => { r.Mode = TransferMode.Move; r.CreateSymlink = true; });

        await ws.Engine.ExecuteAsync(rule, src, dst, false, null, CancellationToken.None);

        // The path still exists, but as a reparse point, so the gate must drop it outright.
        var gate = new Detection.StabilityGate();
        var verdict = gate.Evaluate(src, rule, DateTimeOffset.UtcNow);

        Assert.Equal(Detection.GateStatus.Drop, verdict.Status);
        Assert.Contains("reparse", verdict.Reason);
    }
}
