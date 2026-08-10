using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Journal;
using MSmover.Core.Logging;
using MSmover.Core.Transfer;

namespace MSmover.Core.Engine;

public enum ServiceHealth { Idle, Working, Paused, Error }

/// <summary>
/// Owns the rule runners, the shared transfer engine and the global concurrency budget.
/// Everything the UI needs is exposed as a poll-able snapshot, so no worker thread ever has to
/// marshal onto the UI thread.
/// </summary>
public sealed class MoverService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<RuleRunner> _runners = new();

    public LogHub Log { get; }
    public TransferJournal Journal { get; }
    public AppConfig Config { get; private set; }

    private SemaphoreSlim _globalSlots;
    private TransferEngine _engine;

    public bool Paused => Config.Paused;
    public bool Running { get; private set; }

    public MoverService(AppConfig config, LogHub log)
    {
        Config = config;
        Log = log;
        Journal = new TransferJournal();
        _globalSlots = new SemaphoreSlim(Math.Max(1, config.GlobalMaxConcurrentTransfers));
        _engine = new TransferEngine(config, log, Journal);
    }

    public IReadOnlyList<RuleRunner> Runners { get { lock (_gate) return _runners.ToList(); } }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        lock (_gate)
        {
            if (Running) return;
            AppPaths.EnsureCreated();
            CleanUpOrphans();

            Log.Info($"MSmover starting. {Config.Rules.Count} rule(s) configured." +
                     (Config.GlobalDryRun ? "  GLOBAL DRY RUN IS ON." : "") +
                     (Config.Paused ? "  PAUSED." : ""));

            foreach (var rule in Config.Rules)
            {
                var runner = new RuleRunner(rule, Config, Log, _engine, _globalSlots, Journal);
                _runners.Add(runner);
                if (rule.Enabled) runner.Start();
                if (rule.Enabled && Config.Paused) runner.Pause();
            }

            Running = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!Running) return;
            foreach (var r in _runners) r.Dispose();
            _runners.Clear();
            Running = false;
            Log.Info("MSmover stopped.");
        }
    }

    /// <summary>Applies edited configuration by restarting the runners. Simple and predictable.</summary>
    public void Reload(AppConfig config)
    {
        lock (_gate)
        {
            var wasRunning = Running;
            Stop();
            Config = config;
            _globalSlots = new SemaphoreSlim(Math.Max(1, config.GlobalMaxConcurrentTransfers));
            _engine = new TransferEngine(config, Log, Journal);
            Log.MinimumLevel = config.LogLevel;
            if (wasRunning) Start();
        }
    }

    // ------------------------------------------------------------------ controls

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            Config.Paused = paused;
            foreach (var r in _runners)
            {
                if (paused) r.Pause(); else r.Resume();
            }
            Log.Info(paused ? "Paused. No new transfers will start." : "Resumed.");
        }
    }

    public void ScanNow()
    {
        lock (_gate)
        {
            foreach (var r in _runners) r.RequestScan();
            Log.Info("Manual scan requested for all rules. Everything is re-evaluated from scratch.");
        }
    }

    /// <summary>Puts specific files back in the queue, whatever was previously decided about them.</summary>
    public int Retry(IEnumerable<QueueItem> items)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var group in items.GroupBy(i => i.RuleName, StringComparer.Ordinal))
            {
                var runner = _runners.FirstOrDefault(r => r.Rule.Name == group.Key);
                if (runner is null) continue;
                foreach (var item in group) { runner.Retry(item.Path); count++; }
            }
            return count;
        }
    }

    public void SetGlobalDryRun(bool dryRun)
    {
        lock (_gate)
        {
            Config.GlobalDryRun = dryRun;
            Log.Warn(dryRun
                ? "Global dry run ON - nothing will be written, copied or deleted."
                : "Global dry run OFF - rules not individually in dry run will now transfer for real.");
        }
        Reload(Config);
    }

    // ------------------------------------------------------------------ status

    public IReadOnlyList<QueueItem> SnapshotQueue()
    {
        lock (_gate) return _runners.SelectMany(r => r.Snapshot()).ToList();
    }

    public ServiceHealth Health
    {
        get
        {
            lock (_gate)
            {
                if (Config.Paused) return ServiceHealth.Paused;
                if (_runners.Any(r => r.State == RuleState.Faulted)) return ServiceHealth.Error;
                if (_runners.Any(r => r.InFlightCount > 0)) return ServiceHealth.Working;
                return ServiceHealth.Idle;
            }
        }
    }

    public string StatusLine
    {
        get
        {
            lock (_gate)
            {
                var pending = _runners.Sum(r => r.PendingCount);
                var active = _runners.Sum(r => r.InFlightCount);
                var faulted = _runners.Count(r => r.State == RuleState.Faulted);
                var enabled = _runners.Count(r => r.State is RuleState.Running or RuleState.Paused);

                var bits = new List<string> { $"{enabled} rule(s) active", $"{pending} pending", $"{active} transferring" };
                if (faulted > 0) bits.Add($"{faulted} FAULTED");
                if (Config.GlobalDryRun) bits.Add("GLOBAL DRY RUN");
                if (Config.Paused) bits.Add("PAUSED");
                return string.Join("  |  ", bits);
            }
        }
    }

    // ------------------------------------------------------------------ recovery

    /// <summary>
    /// Deletes .msmover-part files left behind by a crash or power cut. A part file is by
    /// definition incomplete and unverified, so removing it can never lose data.
    /// </summary>
    private void CleanUpOrphans()
    {
        try
        {
            foreach (var part in Journal.FindOrphanedParts())
            {
                try
                {
                    File.Delete(LongPath.Prefix(part));
                    Log.Warn($"Removed an incomplete transfer left by a previous run: {part}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not remove the incomplete file {part}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Orphan clean-up failed: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}
