using System.Collections.Concurrent;
using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Detection;
using MSmover.Core.Logging;
using MSmover.Core.Naming;
using MSmover.Core.Transfer;

namespace MSmover.Core.Engine;

public enum RuleState { Stopped, Running, Paused, Faulted }

/// <summary>
/// One rule: discovery, the completion gate, ordering, retries and dispatch.
///
/// Discovery is deliberately belt-and-braces. FileSystemWatcher is fast but drops events on
/// buffer overflow and is unreliable on network sources, so a periodic full rescan runs
/// regardless and an overflow immediately forces one.
/// </summary>
public sealed class RuleRunner : IDisposable
{
    private const int TickMs = 2000;
    private const int RecentCapacity = 500;

    private readonly AppConfig _app;
    private readonly LogHub _log;
    private readonly TransferEngine _engine;
    private readonly SemaphoreSlim _globalSlots;
    private readonly Journal.TransferJournal _journal;
    private readonly StabilityGate _gate = new();

    private readonly ConcurrentDictionary<string, QueueItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<QueueItem> _recent = new();
    private readonly ConcurrentQueue<string> _incoming = new();

    /// <summary>
    /// Paths already dealt with, so a rescan does not re-queue them. Seeded from the journal at
    /// start-up, which is what stops copy mode re-reporting "target exists" for everything it has
    /// ever copied, every time the app restarts. Cleared by an explicit "Scan now".
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _suppressed = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _lastFullScan = DateTimeOffset.MinValue;
    private volatile bool _rescanRequested;
    private int _inFlight;

    public RuleConfig Rule { get; private set; }
    public RuleState State { get; private set; } = RuleState.Stopped;
    public string Fault { get; private set; } = "";
    public SymlinkCapability? SymlinkStatus { get; private set; }

    public RuleRunner(RuleConfig rule, AppConfig app, LogHub log, TransferEngine engine,
                      SemaphoreSlim globalSlots, Journal.TransferJournal journal)
    {
        Rule = rule;
        _app = app;
        _log = log;
        _engine = engine;
        _globalSlots = globalSlots;
        _journal = journal;
    }

    public bool EffectiveDryRun => _app.GlobalDryRun || Rule.DryRun;

    public IReadOnlyList<QueueItem> Snapshot()
    {
        var live = _items.Values.Select(i => i.Snapshot());
        var recent = _recent.ToArray().Select(i => i.Snapshot());
        return live.Concat(recent).ToList();
    }

    public int PendingCount => _items.Values.Count(i => !i.IsTerminal);
    public int InFlightCount => Volatile.Read(ref _inFlight);

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_loop is not null) return;

        var errors = Rule.Validate();
        if (errors.Count > 0)
        {
            Fault = string.Join(" ", errors);
            State = RuleState.Faulted;
            _log.Error($"Rule not started: {Fault}", Rule.Name);
            return;
        }

        if (!Directory.Exists(LongPath.Prefix(Rule.SourceFolder)))
        {
            Fault = $"Source folder does not exist: {Rule.SourceFolder}";
            State = RuleState.Faulted;
            _log.Error($"Rule not started: {Fault}", Rule.Name);
            return;
        }

        if (!PreflightSymlink()) return;

        SeedSuppressionFromJournal();

        Fault = "";
        _cts = new CancellationTokenSource();
        StartWatcher();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        State = RuleState.Running;

        var mode = EffectiveDryRun ? "DRY RUN" : Rule.Mode.ToString().ToUpperInvariant();
        _log.Info($"Started [{mode}] {Rule.SourceFolder} -> {Rule.TargetFolder}  " +
                  $"(template '{Rule.TargetTemplate}', {(Rule.Recursive ? "recursive" : "top level only")}, " +
                  $"{(Rule.Order == QueueOrder.NewestFirst ? "newest first" : "oldest first")})", Rule.Name);
    }

    /// <summary>
    /// Verifies up front that symlinks can be created AND followed to the target, so a rule can
    /// never move a file and then discover it cannot leave the link behind.
    /// </summary>
    private bool PreflightSymlink()
    {
        if (!Rule.CreateSymlink || Rule.Mode != TransferMode.Move) return true;

        var cap = SymlinkService.Probe(Rule.SourceFolder, Rule.TargetFolder);
        SymlinkStatus = cap;

        if (cap.Usable)
        {
            _log.Info("Symlink pre-flight passed.", Rule.Name);
            return true;
        }

        if (EffectiveDryRun)
        {
            _log.Warn($"Symlink pre-flight failed, but the rule is in dry run so it will still start. {cap.Detail}", Rule.Name);
            return true;
        }

        Fault = cap.Detail;
        State = RuleState.Faulted;
        _log.Error($"Rule not started - symlink pre-flight failed. {cap.Detail}", Rule.Name);
        foreach (var line in SymlinkService.ExplainFailure(cap).Split(Environment.NewLine))
            if (!string.IsNullOrWhiteSpace(line)) _log.Error("  " + line, Rule.Name);
        return false;
    }

    public void Stop()
    {
        _cts?.Cancel();
        StopWatcher();
        try { _loop?.Wait(TimeSpan.FromSeconds(20)); }
        catch (AggregateException) { /* cancellation */ }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        if (State != RuleState.Faulted) State = RuleState.Stopped;
        _log.Info("Stopped.", Rule.Name);
    }

    /// <summary>
    /// Stops new transfers being dispatched. Discovery keeps running so the queue stays current,
    /// and a transfer already in flight is allowed to finish rather than being torn up.
    /// </summary>
    public void Pause()
    {
        if (State == RuleState.Running) State = RuleState.Paused;
    }

    public void Resume()
    {
        if (State == RuleState.Paused) State = RuleState.Running;
    }

    /// <summary>
    /// An explicit "Scan now" also clears the suppression list, so anything previously skipped,
    /// blocked or given up on gets a fresh evaluation once the underlying problem is fixed.
    /// </summary>
    public void RequestScan()
    {
        _suppressed.Clear();
        ClearRecent();
        SeedSuppressionFromJournal();
        _rescanRequested = true;
    }

    private void SeedSuppressionFromJournal()
    {
        try
        {
            foreach (var r in _journal.ReadAll())
                if (r.Event == "done" && r.Rule == Rule.Name && !string.IsNullOrEmpty(r.Source))
                    _suppressed[r.Source] = "already transferred (from journal)";
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not read the journal for suppression state: {ex.Message}", Rule.Name);
        }
    }

    public void Dispose()
    {
        Stop();
        _watcher?.Dispose();
    }

    // ------------------------------------------------------------------ discovery

    private void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(Rule.SourceFolder)
            {
                IncludeSubdirectories = Rule.Recursive,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Created += (_, e) => _incoming.Enqueue(e.FullPath);
            _watcher.Changed += (_, e) => _incoming.Enqueue(e.FullPath);
            _watcher.Renamed += (_, e) => _incoming.Enqueue(e.FullPath);
            _watcher.Error += (_, e) =>
            {
                _log.Warn($"File watcher error, forcing a full rescan: {e.GetException().Message}", Rule.Name);
                _rescanRequested = true;
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not start the file watcher, falling back to periodic scanning only: {ex.Message}", Rule.Name);
            _watcher = null;
        }
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;
        try { _watcher.EnableRaisingEvents = false; } catch { /* shutting down */ }
        _watcher.Dispose();
        _watcher = null;
    }

    private void FullScan()
    {
        _lastFullScan = DateTimeOffset.UtcNow;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = Rule.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(LongPath.Prefix(Rule.SourceFolder), "*", options))
            {
                var clean = LongPath.Strip(path);
                seen.Add(clean);
                Consider(clean);
            }

            // Forget non-terminal items whose file has gone away (deleted or already handled).
            foreach (var kv in _items)
            {
                if (kv.Value.State is ItemState.Transferring) continue;
                if (seen.Contains(kv.Key)) continue;
                if (_items.TryRemove(kv.Key, out _)) _gate.Forget(kv.Key);
            }

            _gate.Retain(_items.Keys.ToList());
        }
        catch (Exception ex)
        {
            _log.Warn($"Scan of {Rule.SourceFolder} failed: {ex.Message}", Rule.Name);
        }
    }

    private void Consider(string path)
    {
        if (path.EndsWith(TransferEngine.PartSuffix, StringComparison.OrdinalIgnoreCase)) return;
        if (path.EndsWith(TransferEngine.LinkSuffix, StringComparison.OrdinalIgnoreCase)) return;
        if (_suppressed.ContainsKey(path)) return;

        FileInfo info;
        try
        {
            info = new FileInfo(LongPath.Prefix(path));
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) return;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
        }
        catch { return; }

        // A file matching neither the include nor the naming rules is not queued at all, so an
        // unrelated folder does not fill the UI with noise. Names that DO match the include regex
        // but fail the naming rule are queued as Skipped, exactly as the old script reported them.
        var map = PathMapper.Map(Rule, path, ChooseStamp(info.LastWriteTime));
        if (map.Verdict is MapVerdict.NotIncluded or MapVerdict.Excluded) return;

        if (_items.TryGetValue(path, out var existing))
        {
            if (existing.State is ItemState.Transferring) return;
            existing.Size = info.Length;
            existing.LastWriteUtc = info.LastWriteTimeUtc;
            return;
        }

        var item = new QueueItem
        {
            Path = path,
            RuleName = Rule.Name,
            Size = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Target = map.FullTarget
        };

        if (!map.Ok)
        {
            item.State = ItemState.Skipped;
            item.Detail = map.Reason;
            if (_items.TryAdd(path, item))
                _log.Warn($"{Path.GetFileName(path)}: {map.Reason}", Rule.Name);
            return;
        }

        if (_items.TryAdd(path, item))
            _log.Debug($"Queued {Path.GetFileName(path)} ({TransferEngine.FormatSize(info.Length)})", Rule.Name);
    }

    private DateTime ChooseStamp(DateTime fileLocalWrite)
        => Rule.DateTokenSource == DateTokenSource.Now ? DateTime.Now : fileLocalWrite;

    // ------------------------------------------------------------------ main loop

    private async Task LoopAsync(CancellationToken ct)
    {
        FullScan();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (_incoming.TryDequeue(out var path)) Consider(path);

                if (_rescanRequested ||
                    DateTimeOffset.UtcNow - _lastFullScan > TimeSpan.FromSeconds(Math.Max(15, Rule.RescanSeconds)))
                {
                    _rescanRequested = false;
                    FullScan();
                }

                if (State == RuleState.Running) Pump(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error($"Rule loop error: {ex.Message}", Rule.Name);
            }

            try { await Task.Delay(TickMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Pump(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var item in _items.Values)
        {
            if (item.State is not (ItemState.Pending or ItemState.Waiting)) continue;
            if (item.NextAttemptUtc > now) continue;

            var gate = _gate.Evaluate(item.Path, Rule, now);
            switch (gate.Status)
            {
                case GateStatus.Ready:
                    item.State = ItemState.Ready;
                    item.Detail = "ready";
                    break;
                case GateStatus.Waiting:
                    item.State = ItemState.Waiting;
                    item.Detail = gate.Reason;
                    break;
                case GateStatus.Drop:
                    _items.TryRemove(item.Path, out _);
                    _gate.Forget(item.Path);
                    break;
            }
        }

        var ready = _items.Values.Where(i => i.State == ItemState.Ready);
        ready = Rule.Order == QueueOrder.NewestFirst
            ? ready.OrderByDescending(i => i.LastWriteUtc)
            : ready.OrderBy(i => i.LastWriteUtc);

        foreach (var item in ready.ToList())
        {
            if (ct.IsCancellationRequested) return;
            if (Volatile.Read(ref _inFlight) >= Rule.Parallelism) return;
            if (!_globalSlots.Wait(0, CancellationToken.None)) return;

            item.State = ItemState.Transferring;
            item.Phase = "starting";
            item.BytesDone = 0;
            Interlocked.Increment(ref _inFlight);
            _ = Task.Run(() => TransferAsync(item, ct), CancellationToken.None);
        }
    }

    private async Task TransferAsync(QueueItem item, CancellationToken ct)
    {
        try
        {
            // Re-map: the mtime may have moved since discovery, and the rule may have been edited.
            DateTime stamp;
            try { stamp = ChooseStamp(File.GetLastWriteTime(LongPath.Prefix(item.Path))); }
            catch { stamp = DateTime.Now; }

            var map = PathMapper.Map(Rule, item.Path, stamp);
            if (!map.Ok)
            {
                item.State = ItemState.Skipped;
                item.Detail = map.Reason;
                _log.Warn($"{item.FileName}: {map.Reason}", Rule.Name);
                Retire(item);
                return;
            }

            item.Target = map.FullTarget;

            var progress = new Progress<TransferProgress>(p =>
            {
                item.Phase = p.Phase;
                item.BytesDone = p.BytesDone;
            });

            var outcome = await _engine.ExecuteAsync(
                Rule, item.Path, map.FullTarget!, EffectiveDryRun, progress, ct).ConfigureAwait(false);

            item.Phase = "";
            item.Detail = outcome.Message;

            switch (outcome.Status)
            {
                case TransferStatus.Transferred:
                case TransferStatus.WouldTransfer:
                    item.State = ItemState.Done;
                    item.BytesDone = item.Size;
                    Retire(item);
                    break;

                case TransferStatus.BlockedTargetExists:
                    item.State = ItemState.Blocked;
                    Retire(item);
                    break;

                case TransferStatus.Cancelled:
                    item.State = ItemState.Waiting;
                    item.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Rule.RetryBackoffSeconds);
                    break;

                case TransferStatus.SourceLocked:
                    // Normal, not a failure: the instrument reopened it or a scanner grabbed it.
                    item.State = ItemState.Waiting;
                    item.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Rule.StabilityIntervalSeconds);
                    break;

                case TransferStatus.Failed:
                    item.Attempts++;
                    if (item.Attempts > Rule.MaxRetries)
                    {
                        item.State = ItemState.Failed;
                        _log.Error($"{item.FileName}: giving up after {item.Attempts} attempts. " +
                                   $"Source is untouched. Last error: {outcome.Message}", Rule.Name);
                        Retire(item);
                    }
                    else
                    {
                        var wait = Rule.RetryBackoffSeconds * item.Attempts;
                        item.State = ItemState.Waiting;
                        item.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(wait);
                        _log.Warn($"{item.FileName}: attempt {item.Attempts}/{Rule.MaxRetries} failed, " +
                                  $"retrying in {wait}s. {outcome.Message}", Rule.Name);
                    }
                    break;
            }

            // In dry run nothing changes on disk, so a completed item would be rediscovered on
            // every scan. Suppressing the repeat keeps the log readable.
            if (EffectiveDryRun && item.State == ItemState.Done)
                item.Detail = "dry run - would have transferred";
        }
        catch (Exception ex)
        {
            item.State = ItemState.Waiting;
            item.Detail = ex.Message;
            item.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Rule.RetryBackoffSeconds);
            _log.Error($"{item.FileName}: unexpected error: {ex}", Rule.Name);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _globalSlots.Release();
        }
    }

    /// <summary>Moves a terminal item out of the active set into the bounded recent list.</summary>
    private void Retire(QueueItem item)
    {
        item.CompletedUtc = DateTimeOffset.UtcNow;
        if (!_items.TryRemove(item.Path, out _)) return;
        _gate.Forget(item.Path);

        // Copy mode leaves the source in place, and dry run leaves everything in place, so without
        // this the next scan would re-queue the same file forever. "Scan now" clears it.
        _suppressed[item.Path] = item.State.ToString();

        _recent.Enqueue(item);
        while (_recent.Count > RecentCapacity) _recent.TryDequeue(out _);
    }

    public void ClearRecent()
    {
        while (_recent.TryDequeue(out _)) { }
    }
}
