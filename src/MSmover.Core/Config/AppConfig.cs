using MSmover.Core.Logging;

namespace MSmover.Core.Config;

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    public List<RuleConfig> Rules { get; set; } = new();

    /// <summary>Master override. When true, no rule can write anything, whatever its own setting.</summary>
    public bool GlobalDryRun { get; set; }

    /// <summary>Paused state survives a restart, so a deliberate pause is not undone by a reboot.</summary>
    public bool Paused { get; set; }

    public bool StartMinimised { get; set; } = true;
    public bool AutoStartWithWindows { get; set; }

    /// <summary>Ceiling across all rules, so several rules cannot saturate the link between them.</summary>
    public int GlobalMaxConcurrentTransfers { get; set; } = 2;

    public int LogRetentionDays { get; set; } = 14;
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>Copy buffer in bytes. 1 MiB is a good fit for SMB with 0.1-2 GB files.</summary>
    public int CopyChunkBytes { get; set; } = 1024 * 1024;
}
