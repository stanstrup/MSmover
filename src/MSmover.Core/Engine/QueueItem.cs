namespace MSmover.Core.Engine;

public enum ItemState
{
    /// <summary>Discovered, not yet evaluated.</summary>
    Pending,
    /// <summary>Discovered but not finished being written. Detail says which check is holding it.</summary>
    Waiting,
    /// <summary>All checks passed, queued for a transfer slot.</summary>
    Ready,
    Transferring,
    Done,
    /// <summary>A file already exists at the target. Terminal; needs a human.</summary>
    Blocked,
    /// <summary>Retries exhausted. Terminal; source is untouched.</summary>
    Failed,
    /// <summary>The filename did not satisfy the naming rule. Terminal.</summary>
    Skipped
}

public sealed class QueueItem
{
    public required string Path { get; init; }
    public required string RuleName { get; init; }

    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }

    public ItemState State { get; set; } = ItemState.Pending;
    public string Detail { get; set; } = "";
    public string? Target { get; set; }

    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset DiscoveredUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }

    public string Phase { get; set; } = "";
    public long BytesDone { get; set; }

    public double ProgressFraction => Size > 0 ? Math.Clamp((double)BytesDone / Size, 0, 1) : 0;

    public string FileName => System.IO.Path.GetFileName(Path);

    public bool IsTerminal => State is ItemState.Done or ItemState.Blocked or ItemState.Failed or ItemState.Skipped;

    public QueueItem Snapshot() => (QueueItem)MemberwiseClone();
}
