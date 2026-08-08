using MSmover.Core.Config;
using MSmover.Core.Transfer;
using Xunit;

namespace MSmover.Core.Tests;

/// <summary>
/// The invariant these tests exist to protect: a source file is never deleted unless a verified,
/// correctly named copy exists at the destination.
/// </summary>
public class TransferEngineTests
{
    [Fact]
    public async Task Copy_leaves_the_source_in_place_and_writes_a_byte_identical_target()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_001.raw", 300_000);
        var rule = ws.Rule();
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_001.raw");

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Transferred, result.Status);
        Assert.True(File.Exists(src));
        Assert.True(File.Exists(dst));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dst));
        Assert.False(File.Exists(dst + TransferEngine.PartSuffix));
    }

    [Fact]
    public async Task Move_deletes_the_source_only_after_the_target_is_verified()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_002.raw", 200_000);
        var original = File.ReadAllBytes(src);
        var rule = ws.Rule(r => r.Mode = TransferMode.Move);
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_002.raw");

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Transferred, result.Status);
        Assert.False(File.Exists(src));
        Assert.Equal(original, File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task An_existing_target_blocks_the_transfer_and_never_touches_either_file()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_003.raw", 50_000);
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_003.raw");
        File.WriteAllText(dst, "pre-existing content that must survive");
        var before = File.ReadAllText(dst);

        var rule = ws.Rule(r => r.Mode = TransferMode.Move);
        var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.BlockedTargetExists, result.Status);
        Assert.True(File.Exists(src));
        Assert.Equal(before, File.ReadAllText(dst));
        Assert.Contains("Target already exists", ws.LogText());
    }

    [Fact]
    public async Task Verification_failure_deletes_the_partial_target_and_keeps_the_source()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_004.raw", 100_000);
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_004.raw");

        // An external copier that produces the wrong bytes stands in for a corrupted transfer.
        var rule = ws.Rule(r =>
        {
            r.Mode = TransferMode.Move;
            r.ExternalCommand = "echo corrupted> \"{dst}\"";
        });

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Failed, result.Status);
        Assert.True(File.Exists(src));                              // the whole point
        Assert.False(File.Exists(dst));
        Assert.False(File.Exists(dst + TransferEngine.PartSuffix));
        Assert.Contains("length mismatch", ws.LogText());
    }

    [Fact]
    public async Task A_correct_external_copier_is_accepted_after_verification()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_005.raw", 100_000);
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_005.raw");

        var rule = ws.Rule(r => r.ExternalCommand = "copy /y \"{src}\" \"{dst}\" >nul");

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Transferred, result.Status);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task Dry_run_reports_the_target_without_creating_anything()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_006.raw", 10_000);
        var dst = Path.Combine(ws.Target, "proj", "SAMPLE_A01_006.raw");
        var rule = ws.Rule(r => r.Mode = TransferMode.Move);

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: true, null, CancellationToken.None);

        Assert.Equal(TransferStatus.WouldTransfer, result.Status);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dst));
        Assert.False(Directory.Exists(Path.GetDirectoryName(dst)!));
        Assert.Contains("DRY RUN  WOULD MOVE", ws.LogText());
    }

    [Fact]
    public async Task Cancelling_mid_copy_removes_the_partial_file_and_keeps_the_source()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_007.raw", 8_000_000);
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_007.raw");
        var rule = ws.Rule(r => r.Mode = TransferMode.Move);

        using var cts = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(p =>
        {
            if (p.Phase == "copy" && p.BytesDone > 0) cts.Cancel();
        });

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: false, progress, cts.Token);

        Assert.Equal(TransferStatus.Cancelled, result.Status);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dst));
        Assert.False(File.Exists(dst + TransferEngine.PartSuffix));
    }

    [Fact]
    public async Task A_locked_source_is_reported_as_locked_rather_than_failed()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_008.raw", 10_000);
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_008.raw");
        var rule = ws.Rule();

        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await ws.Engine.ExecuteAsync(rule, src, dst, dryRun: false, null, CancellationToken.None);

            Assert.Equal(TransferStatus.SourceLocked, result.Status);
            Assert.True(result.ShouldRetry);
            Assert.False(File.Exists(dst));
        }
    }

    [Fact]
    public async Task Timestamps_are_carried_across()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("SAMPLE_A01_009.raw", 5_000);
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, when);
        var dst = Path.Combine(ws.Target, "SAMPLE_A01_009.raw");

        await ws.Engine.ExecuteAsync(ws.Rule(), src, dst, dryRun: false, null, CancellationToken.None);

        Assert.Equal(when, File.GetLastWriteTimeUtc(dst));
    }

    [Fact]
    public async Task Nested_target_folders_are_created()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile("MSTEST_A01_010.raw", 5_000);
        var dst = Path.Combine(ws.Target, "MSTEST", "MSTEST.pro", "Data", "MSTEST_A01_010.raw");

        var result = await ws.Engine.ExecuteAsync(ws.Rule(), src, dst, dryRun: false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Transferred, result.Status);
        Assert.True(File.Exists(dst));
    }

    [Fact]
    public async Task The_index_file_records_every_completed_transfer()
    {
        using var ws = new TempWorkspace();
        var rule = ws.Rule(r => r.IndexFile = "msmover_index.tsv");

        foreach (var n in new[] { "A_1_1.raw", "B_1_1.raw" })
        {
            var src = ws.WriteFile(n, 4_000);
            await ws.Engine.ExecuteAsync(rule, src, Path.Combine(ws.Target, n), false, null, CancellationToken.None);
        }

        var lines = File.ReadAllLines(Path.Combine(ws.Target, "msmover_index.tsv"));
        Assert.Equal(3, lines.Length);                     // header + 2
        Assert.StartsWith("timestamp\trule\ttarget", lines[0]);
        Assert.Contains("A_1_1.raw", lines[1]);
        Assert.Contains("B_1_1.raw", lines[2]);
    }

    [Theory]
    [InlineData(HashKind.XxHash64)]
    [InlineData(HashKind.Sha256)]
    [InlineData(HashKind.Md5)]
    public async Task All_hash_algorithms_verify_a_good_copy(HashKind kind)
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile($"H_{kind}_1.raw", 250_000);
        var dst = Path.Combine(ws.Target, Path.GetFileName(src));

        var result = await ws.Engine.ExecuteAsync(
            ws.Rule(r => r.HashAlgorithm = kind), src, dst, false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Transferred, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Hash));
    }

    [Fact]
    public async Task Move_mode_prunes_empty_source_folders_when_asked()
    {
        using var ws = new TempWorkspace();
        var src = ws.WriteFile(Path.Combine("2026", "week12", "N_1_1.raw"), 4_000);
        var rule = ws.Rule(r =>
        {
            r.Mode = TransferMode.Move;
            r.Recursive = true;
            r.DeleteEmptySourceDirs = true;
        });

        await ws.Engine.ExecuteAsync(rule, src, Path.Combine(ws.Target, "N_1_1.raw"), false, null, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(ws.Source, "2026", "week12")));
        Assert.False(Directory.Exists(Path.Combine(ws.Source, "2026")));
        Assert.True(Directory.Exists(ws.Source));       // never the root itself
    }

    [Fact]
    public void Orphaned_part_files_from_an_interrupted_run_are_found_for_clean_up()
    {
        using var ws = new TempWorkspace();
        var part = Path.Combine(ws.Target, "interrupted.raw" + TransferEngine.PartSuffix);
        File.WriteAllText(part, "half a file");

        ws.Journal.Append(new Core.Journal.JournalRecord
        {
            Event = "start", Rule = "test", Source = @"C:\in\interrupted.raw",
            Target = Path.Combine(ws.Target, "interrupted.raw"), Part = part
        });

        Assert.Contains(part, ws.Journal.FindOrphanedParts());

        ws.Journal.Append(new Core.Journal.JournalRecord { Event = "done", Rule = "test", Part = part });

        Assert.DoesNotContain(part, ws.Journal.FindOrphanedParts());
    }
}
