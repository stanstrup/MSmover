using System.Collections.Concurrent;
using MSmover.Core.Common;
using MSmover.Core.Config;

namespace MSmover.Core.Detection;

public enum GateStatus
{
    /// <summary>Every check passed. The file may be transferred.</summary>
    Ready,
    /// <summary>Not finished yet. Try again on the next tick.</summary>
    Waiting,
    /// <summary>Will never be eligible (symlink, vanished). Drop it from the queue.</summary>
    Drop
}

public readonly record struct GateResult(GateStatus Status, string Reason)
{
    public static GateResult Ready() => new(GateStatus.Ready, "ready");
    public static GateResult Waiting(string why) => new(GateStatus.Waiting, why);
    public static GateResult Drop(string why) => new(GateStatus.Drop, why);
}

/// <summary>
/// Decides whether a file has finished being written.
///
/// Deliberately belt-and-braces: a false negative costs a few minutes of delay, a false
/// positive costs data. All of the following must hold before a file is Ready.
///
///   1. not a reparse point (never re-process our own symlinks)
///   2. at least MinSizeBytes
///   3. last write at least MinAgeSeconds ago
///   4. the optional companion file exists
///   5. size unchanged across StabilityProbes consecutive probes, StabilityIntervalSeconds apart
///   6. the file can be opened with FileShare.None
/// </summary>
public sealed class StabilityGate
{
    private sealed class Probe
    {
        public long LastSize;
        public int StableCount;
        public DateTimeOffset LastProbeUtc = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, Probe> _probes =
        new(StringComparer.OrdinalIgnoreCase);

    public GateResult Evaluate(string path, RuleConfig rule, DateTimeOffset nowUtc)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(LongPath.Prefix(path));
            if (!info.Exists) return GateResult.Drop("file no longer exists");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return GateResult.Drop($"cannot stat file: {ex.Message}");
        }

        if (FileGuard.IsReparsePoint(path))
            return GateResult.Drop("symlink or reparse point");

        if (info.Length < rule.MinSizeBytes)
            return GateResult.Waiting($"below minimum size ({info.Length} < {rule.MinSizeBytes} bytes)");

        var age = nowUtc - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (age < TimeSpan.FromSeconds(rule.MinAgeSeconds))
            return GateResult.Waiting($"too recent ({(int)age.TotalSeconds}s < {rule.MinAgeSeconds}s)");

        if (!string.IsNullOrWhiteSpace(rule.RequireSiblingGlob))
        {
            var glob = Naming.PathMapper.ExpandSiblingGlob(rule.RequireSiblingGlob, path);
            var dir = Path.GetDirectoryName(path);
            var found = false;
            try
            {
                if (dir is not null)
                    found = Directory.EnumerateFileSystemEntries(LongPath.Prefix(dir), glob).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return GateResult.Waiting($"cannot check companion file '{glob}': {ex.Message}");
            }
            if (!found) return GateResult.Waiting($"companion file '{glob}' not present yet");
        }

        var probe = _probes.GetOrAdd(path, _ => new Probe());
        lock (probe)
        {
            var interval = TimeSpan.FromSeconds(rule.StabilityIntervalSeconds);
            var sinceLast = nowUtc - probe.LastProbeUtc;

            if (probe.LastProbeUtc != DateTimeOffset.MinValue && sinceLast < interval)
                return GateResult.Waiting($"size check {probe.StableCount}/{rule.StabilityProbes}, next probe in {(int)(interval - sinceLast).TotalSeconds}s");

            if (probe.LastProbeUtc != DateTimeOffset.MinValue && probe.LastSize == info.Length)
                probe.StableCount++;
            else
                probe.StableCount = 1;

            probe.LastSize = info.Length;
            probe.LastProbeUtc = nowUtc;

            if (probe.StableCount < rule.StabilityProbes)
                return GateResult.Waiting($"size check {probe.StableCount}/{rule.StabilityProbes} ({info.Length} bytes)");
        }

        // Last and strongest: nothing else may hold a handle.
        if (!FileGuard.IsUnlocked(path))
            return GateResult.Waiting("file is locked by another process");

        return GateResult.Ready();
    }

    public void Forget(string path) => _probes.TryRemove(path, out _);

    public void Clear() => _probes.Clear();

    /// <summary>Drops probe state for paths no longer in the queue, so memory does not creep.</summary>
    public void Retain(IReadOnlyCollection<string> livePaths)
    {
        var live = new HashSet<string>(livePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _probes.Keys)
            if (!live.Contains(key))
                _probes.TryRemove(key, out _);
    }
}
