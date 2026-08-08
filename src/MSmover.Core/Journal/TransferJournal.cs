using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MSmover.Core.Common;

namespace MSmover.Core.Journal;

public sealed record JournalRecord
{
    public string Ts { get; init; } = DateTimeOffset.Now.ToString("O");
    public string Event { get; init; } = "";      // start | done | fail | block | skip
    public string Rule { get; init; } = "";
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public string Part { get; init; } = "";
    public long Size { get; init; }
    public string Hash { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>
/// Append-only JSONL record of everything the engine did.
///
/// Its operational job is crash recovery: a "start" record carries the .msmover-part path, so on
/// the next launch any part file without a matching terminal record can be deleted. Without that,
/// an interrupted 2 GB transfer would leave debris on the network share forever.
/// </summary>
public sealed class TransferJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private const long RotateBytes = 20L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;

    public TransferJournal(string? path = null)
    {
        _path = path ?? AppPaths.JournalFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Append(JournalRecord record)
    {
        lock (_gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(_path, JsonSerializer.Serialize(record, Json) + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // The journal is a safety net, not a dependency. Never let it break a transfer.
            }
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < RotateBytes) return;
        var archived = _path + ".1";
        try
        {
            if (File.Exists(archived)) File.Delete(archived);
            File.Move(_path, archived);
        }
        catch { /* best effort */ }
    }

    public IReadOnlyList<JournalRecord> ReadAll()
    {
        lock (_gate)
        {
            var results = new List<JournalRecord>();
            foreach (var file in new[] { _path + ".1", _path })
            {
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var r = JsonSerializer.Deserialize<JournalRecord>(line, Json);
                        if (r is not null) results.Add(r);
                    }
                    catch (JsonException) { /* skip a torn final line */ }
                }
            }
            return results;
        }
    }

    /// <summary>
    /// Part files from "start" records that never got a terminal record. These are the leftovers
    /// of a crash or a power cut and are always safe to delete: a part file is by definition
    /// incomplete and unverified.
    /// </summary>
    public IReadOnlyList<string> FindOrphanedParts()
    {
        var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ReadAll())
        {
            if (string.IsNullOrEmpty(r.Part)) continue;
            if (r.Event == "start") open[r.Part] = r.Source;
            else open.Remove(r.Part);
        }
        return open.Keys.Where(p => File.Exists(LongPath.Prefix(p))).ToList();
    }
}
