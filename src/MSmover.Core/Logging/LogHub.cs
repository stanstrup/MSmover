using System.Text;
using MSmover.Core.Common;

namespace MSmover.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(long Seq, DateTimeOffset Time, LogLevel Level, string Rule, string Message)
{
    public string Format() =>
        $"{Time.LocalDateTime:yyyy-MM-dd HH:mm:ss} {Level.ToString().ToUpperInvariant(),-5} " +
        $"{(string.IsNullOrEmpty(Rule) ? "-" : Rule)} | {Message}";
}

/// <summary>
/// Rolling daily file log plus a bounded in-memory ring buffer.
///
/// The UI polls <see cref="GetSince"/> rather than subscribing to an event, which keeps all
/// cross-thread marshalling out of the picture: worker threads only ever append.
/// </summary>
public sealed class LogHub : IDisposable
{
    private readonly object _gate = new();
    private readonly LogEntry?[] _ring;
    private readonly int _capacity;
    private readonly int _retentionDays;
    private long _seq;
    private StreamWriter? _writer;
    private DateOnly _writerDay;
    private bool _disposed;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public LogHub(int capacity = 5000, int retentionDays = 14)
    {
        _capacity = capacity;
        _ring = new LogEntry?[capacity];
        _retentionDays = retentionDays;
        try
        {
            AppPaths.EnsureCreated();
            PurgeOldFiles();
        }
        catch
        {
            // Logging must never be the reason the app fails to start.
        }
    }

    public void Debug(string message, string rule = "") => Write(LogLevel.Debug, rule, message);
    public void Info(string message, string rule = "") => Write(LogLevel.Info, rule, message);
    public void Warn(string message, string rule = "") => Write(LogLevel.Warn, rule, message);
    public void Error(string message, string rule = "") => Write(LogLevel.Error, rule, message);

    public void Write(LogLevel level, string rule, string message)
    {
        if (level < MinimumLevel) return;

        LogEntry entry;
        lock (_gate)
        {
            if (_disposed) return;
            entry = new LogEntry(++_seq, DateTimeOffset.Now, level, rule, message);
            _ring[(int)((_seq - 1) % _capacity)] = entry;
            TryWriteToFile(entry);
        }
    }

    /// <summary>Entries with Seq &gt; <paramref name="afterSeq"/>, oldest first.</summary>
    public IReadOnlyList<LogEntry> GetSince(long afterSeq)
    {
        lock (_gate)
        {
            var oldest = Math.Max(afterSeq, _seq - _capacity);
            if (oldest >= _seq) return Array.Empty<LogEntry>();

            var result = new List<LogEntry>((int)(_seq - oldest));
            for (var s = oldest + 1; s <= _seq; s++)
            {
                var e = _ring[(int)((s - 1) % _capacity)];
                if (e is not null) result.Add(e);
            }
            return result;
        }
    }

    public long CurrentSequence { get { lock (_gate) return _seq; } }

    private void TryWriteToFile(LogEntry entry)
    {
        try
        {
            var day = DateOnly.FromDateTime(entry.Time.LocalDateTime);
            if (_writer is null || day != _writerDay)
            {
                _writer?.Dispose();
                AppPaths.EnsureCreated();
                var file = Path.Combine(AppPaths.LogDirectory, $"msmover-{day:yyyyMMdd}.log");
                _writer = new StreamWriter(file, append: true, Encoding.UTF8) { AutoFlush = true };
                _writerDay = day;
                PurgeOldFiles();
            }
            _writer.WriteLine(entry.Format());
        }
        catch
        {
            // A locked or unwritable log file must not take the transfer engine down.
        }
    }

    private void PurgeOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-_retentionDays);
        foreach (var f in Directory.EnumerateFiles(AppPaths.LogDirectory, "msmover-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
            catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
