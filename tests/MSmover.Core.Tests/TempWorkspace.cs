using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Journal;
using MSmover.Core.Logging;
using MSmover.Core.Transfer;

namespace MSmover.Core.Tests;

/// <summary>Isolated source/target/state folders for one test, torn down afterwards.</summary>
public sealed class TempWorkspace : IDisposable
{
    public string Root { get; }
    public string Source { get; }
    public string Target { get; }
    public LogHub Log { get; }
    public TransferJournal Journal { get; }
    public AppConfig App { get; }
    public TransferEngine Engine { get; }

    private readonly string _previousAppRoot;

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "msmover-tests", Guid.NewGuid().ToString("N"));
        Source = Path.Combine(Root, "in");
        Target = Path.Combine(Root, "out");
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);

        _previousAppRoot = AppPaths.Root;
        AppPaths.Root = Path.Combine(Root, "state");
        AppPaths.EnsureCreated();

        Log = new LogHub(capacity: 500, retentionDays: 1) { MinimumLevel = LogLevel.Debug };
        Journal = new TransferJournal(Path.Combine(AppPaths.Root, "journal.jsonl"));
        App = new AppConfig { CopyChunkBytes = 64 * 1024 };
        Engine = new TransferEngine(App, Log, Journal);
    }

    public RuleConfig Rule(Action<RuleConfig>? tweak = null)
    {
        var rule = new RuleConfig
        {
            Name = "test",
            Enabled = true,
            SourceFolder = Source,
            TargetFolder = Target,
            IncludeRegex = @"(?i)\.raw$",
            Delimiter = "_",
            ExpectedDelimiterCount = null,
            TargetTemplate = "{filename}",
            MinAgeSeconds = 0,
            StabilityProbes = 1,
            StabilityIntervalSeconds = 1,
            MinSizeBytes = 1,
            DryRun = false,
            Mode = TransferMode.Copy
        };
        tweak?.Invoke(rule);
        return rule;
    }

    public string WriteFile(string name, int sizeBytes, byte seed = 0x5A)
    {
        var path = Path.Combine(Source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[sizeBytes];
        for (var i = 0; i < sizeBytes; i++) data[i] = (byte)(seed + (i % 251));
        File.WriteAllBytes(path, data);
        return path;
    }

    public string LogText() => string.Join(Environment.NewLine, Log.GetSince(0).Select(e => e.Format()));

    public void Dispose()
    {
        Log.Dispose();
        AppPaths.Root = _previousAppRoot;
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
