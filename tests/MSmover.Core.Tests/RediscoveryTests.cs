using MSmover.Core.Config;
using MSmover.Core.Engine;
using MSmover.Core.Journal;
using MSmover.Core.Logging;
using MSmover.Core.Transfer;
using Xunit;

namespace MSmover.Core.Tests;

/// <summary>
/// Regression tests for the rule that used to be "we transferred this source path once, never
/// look at it again". That memory was keyed on the source alone and was reloaded from the journal
/// even by an explicit "Scan now", so deleting a file at the target left its source permanently
/// invisible with no way to recover short of deleting journal.jsonl.
///
/// The rule now is: a file is skipped only while a previous transfer of it is *still present* at
/// the target. Delete the target and it comes back.
/// </summary>
public class RediscoveryTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly LogHub _log;
    private readonly TransferJournal _journal;
    private readonly MoverService _service;
    private readonly AppConfig _config;

    public RediscoveryTests()
    {
        _log = new LogHub(capacity: 500, retentionDays: 1) { MinimumLevel = LogLevel.Debug };
        _journal = new TransferJournal(Path.Combine(Common.AppPaths.Root, "journal.jsonl"));
        _config = new AppConfig();
        _config.Rules.Add(new RuleConfig
        {
            Name = "rediscovery",
            Enabled = true,
            SourceFolder = _ws.Source,
            TargetFolder = _ws.Target,
            IncludeRegex = @"(?i)\.raw$",
            ExpectedDelimiterCount = null,
            TargetTemplate = "{filename}",
            MinAgeSeconds = 0,
            StabilityProbes = 1,
            StabilityIntervalSeconds = 1,
            MinSizeBytes = 1,
            Mode = TransferMode.Copy,
            DryRun = false
        });
        _service = new MoverService(_config, _log);
    }

    public void Dispose()
    {
        _service.Dispose();
        _log.Dispose();
        _ws.Dispose();
    }

    private RuleRunner Runner => _service.Runners[0];

    /// <summary>
    /// Waits for a specific condition rather than for the queue to look idle. The queue is
    /// momentarily idle between a rescan being requested and the loop's next tick, so settling on
    /// emptiness passes before any work has been attempted.
    /// </summary>
    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan? limit = null)
    {
        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(40));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    /// <summary>Gives the rule long enough to have acted, for asserting that it did not.</summary>
    private static Task QuietPeriod() => Task.Delay(TimeSpan.FromSeconds(8));

    [Fact]
    public async Task Deleting_the_target_makes_the_source_eligible_again()
    {
        var source = _ws.WriteFile("REDISCOVER_A01_001.raw", 40_000);
        var target = Path.Combine(_ws.Target, "REDISCOVER_A01_001.raw");

        _service.Start();
        Assert.True(await WaitUntil(() => File.Exists(target)), "the first transfer should have happened");

        // A rescan while the target is present must not re-transfer or re-report it.
        var firstWriteTicks = File.GetLastWriteTimeUtc(target).Ticks;
        _service.ScanNow();
        await QuietPeriod();
        Assert.Equal(firstWriteTicks, File.GetLastWriteTimeUtc(target).Ticks);

        // The case that was broken: delete the target, and the source must come back on its own.
        File.Delete(target);
        _service.ScanNow();

        Assert.True(await WaitUntil(() => File.Exists(target)),
            "deleting the target should make the source eligible again");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task A_restart_does_not_resurrect_the_old_never_touch_again_memory()
    {
        var source = _ws.WriteFile("REDISCOVER_A01_002.raw", 30_000);
        var target = Path.Combine(_ws.Target, "REDISCOVER_A01_002.raw");

        _service.Start();
        Assert.True(await WaitUntil(() => File.Exists(target)));

        _service.Stop();
        File.Delete(target);

        // The journal still records the earlier transfer; that must not stop a fresh session
        // seeing the source, now that the target is gone.
        _service.Start();

        Assert.True(await WaitUntil(() => File.Exists(target)),
            "a restart must not resurrect a permanent 'already handled' memory");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task A_target_file_we_did_not_put_there_is_reported_rather_than_silently_skipped()
    {
        _ws.WriteFile("REDISCOVER_A01_003.raw", 20_000);
        var target = Path.Combine(_ws.Target, "REDISCOVER_A01_003.raw");
        File.WriteAllText(target, "someone else's file");

        _service.Start();
        Assert.True(await WaitUntil(() => _service.SnapshotQueue().Any(i => i.State == ItemState.Blocked)),
            "a file we did not put at the target must be surfaced, not silently skipped");

        // Untouched, and the source is still there.
        Assert.Equal("someone else's file", File.ReadAllText(target));
        Assert.Contains("Target already exists",
            string.Join("\n", _log.GetSince(0).Select(e => e.Format())));
    }

    [Fact]
    public async Task Retry_puts_a_finished_file_back_in_the_queue()
    {
        var source = _ws.WriteFile("REDISCOVER_A01_004.raw", 20_000);
        var target = Path.Combine(_ws.Target, "REDISCOVER_A01_004.raw");

        _service.Start();
        Assert.True(await WaitUntil(() => File.Exists(target)));
        Assert.True(await WaitUntil(() => _service.SnapshotQueue().Any(i => i.State == ItemState.Done)));

        File.Delete(target);

        var done = _service.SnapshotQueue().Where(i => i.State == ItemState.Done).ToList();
        Assert.Equal(1, _service.Retry(done));

        Assert.True(await WaitUntil(() => File.Exists(target)), "Retry should transfer the file again");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task The_same_file_never_appears_twice_in_the_queue_after_a_retry()
    {
        _ws.WriteFile("REDISCOVER_A01_005.raw", 20_000);

        _service.Start();
        Assert.True(await WaitUntil(() => _service.SnapshotQueue().Any(i => i.State == ItemState.Done)));

        var done = _service.SnapshotQueue().Where(i => i.State == ItemState.Done).ToList();
        _service.Retry(done);

        var snapshot = _service.SnapshotQueue();
        var duplicated = snapshot.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicated);
    }

    [Fact]
    public async Task Move_mode_leaves_nothing_behind_to_rediscover()
    {
        _config.Rules[0].Mode = TransferMode.Move;
        var source = _ws.WriteFile("REDISCOVER_A01_006.raw", 20_000);
        var target = Path.Combine(_ws.Target, "REDISCOVER_A01_006.raw");

        _service.Start();
        Assert.True(await WaitUntil(() => File.Exists(target)));

        Assert.False(File.Exists(source), "move mode should have deleted the source");

        _service.ScanNow();
        await QuietPeriod();
        Assert.DoesNotContain(_service.SnapshotQueue(), i => !i.IsTerminal);
    }
}
