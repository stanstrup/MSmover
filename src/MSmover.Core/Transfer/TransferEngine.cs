using System.Diagnostics;
using System.Text;
using MSmover.Core.Common;
using MSmover.Core.Config;
using MSmover.Core.Journal;
using MSmover.Core.Logging;

namespace MSmover.Core.Transfer;

public enum TransferStatus
{
    Transferred,
    /// <summary>Dry run: reported what would have happened, touched nothing.</summary>
    WouldTransfer,
    /// <summary>A file already exists at the target. Source left alone, per configuration.</summary>
    BlockedTargetExists,
    /// <summary>Could not take an exclusive lock. Try again later.</summary>
    SourceLocked,
    Failed,
    Cancelled
}

public sealed record TransferOutcome(
    TransferStatus Status,
    string Message,
    string? TargetPath = null,
    long Bytes = 0,
    string? Hash = null,
    TimeSpan Elapsed = default)
{
    public bool ShouldRetry => Status is TransferStatus.SourceLocked or TransferStatus.Failed;
}

public sealed record TransferProgress(string Phase, long BytesDone, long BytesTotal);

/// <summary>
/// The safety-critical part. The ordering below is the whole point of this application:
///
///   1. take an exclusive lock on the source and keep it for the whole read
///   2. refuse if a file already exists at the target
///   3. stream into "&lt;name&gt;.msmover-part", hashing the source in the same pass
///   4. flush to the device
///   5. re-read the part file back FROM THE DESTINATION and hash it independently
///   6. compare length and hash; on any mismatch delete the part and stop
///   7. rename the part to its final name
///   8. move mode only: create the symlink, then delete the source, then rename the link
///
/// There is no code path that deletes a source file which has not been byte-verified at the
/// destination, and no code path that deletes anything else in the target folder.
/// </summary>
public sealed class TransferEngine
{
    private readonly AppConfig _app;
    private readonly LogHub _log;
    private readonly TransferJournal _journal;
    private readonly object _indexGate = new();

    public const string PartSuffix = ".msmover-part";
    public const string LinkSuffix = ".msmover-link";

    /// <summary>
    /// Test seam. Creating a real symlink needs SeCreateSymbolicLinkPrivilege, which build agents
    /// do not have, and this is the one code path that deletes a source file - so it has to be
    /// exercisable without that privilege.
    /// </summary>
    internal static Action<string, string> CreateSymlink = SymlinkService.Create;

    public TransferEngine(AppConfig app, LogHub log, TransferJournal journal)
    {
        _app = app;
        _log = log;
        _journal = journal;
    }

    public async Task<TransferOutcome> ExecuteAsync(
        RuleConfig rule,
        string sourcePath,
        string targetPath,
        bool dryRun,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var verb = rule.Mode == TransferMode.Move ? "MOVE" : "COPY";

        if (dryRun)
        {
            var exists = File.Exists(LongPath.Prefix(targetPath));
            var note = exists ? "  [target already exists - would be SKIPPED]" : "";
            var link = rule is { Mode: TransferMode.Move, CreateSymlink: true } ? "  [+symlink back]" : "";
            _log.Info($"DRY RUN  WOULD {verb}  {sourcePath}  ->  {targetPath}{link}{note}", rule.Name);
            return new TransferOutcome(
                exists ? TransferStatus.BlockedTargetExists : TransferStatus.WouldTransfer,
                exists ? "target already exists" : $"would {verb.ToLowerInvariant()}",
                targetPath, Elapsed: sw.Elapsed);
        }

        // ---- 1. exclusive lock, held for the whole read -------------------------------------
        var src = FileGuard.TryOpenExclusive(sourcePath);
        if (src is null)
            return new TransferOutcome(TransferStatus.SourceLocked, "source is locked by another process");

        var partPath = targetPath + PartSuffix;
        var sourceLength = src.Length;
        DateTime sourceWriteUtc, sourceCreateUtc;
        try
        {
            sourceWriteUtc = File.GetLastWriteTimeUtc(LongPath.Prefix(sourcePath));
            sourceCreateUtc = File.GetCreationTimeUtc(LongPath.Prefix(sourcePath));
        }
        catch (Exception ex)
        {
            src.Dispose();
            return new TransferOutcome(TransferStatus.Failed, $"cannot read source timestamps: {ex.Message}");
        }

        try
        {
            // ---- 2. never overwrite ----------------------------------------------------------
            var targetDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(LongPath.Prefix(targetDir));

            if (File.Exists(LongPath.Prefix(targetPath)))
            {
                _log.Warn($"Target already exists, source left untouched: {targetPath}", rule.Name);
                _journal.Append(new JournalRecord
                {
                    Event = "block", Rule = rule.Name, Source = sourcePath,
                    Target = targetPath, Mode = verb, Detail = "target exists"
                });
                return new TransferOutcome(TransferStatus.BlockedTargetExists,
                    "a file already exists at the target", targetPath, Elapsed: sw.Elapsed);
            }

            _journal.Append(new JournalRecord
            {
                Event = "start", Rule = rule.Name, Source = sourcePath, Target = targetPath,
                Part = partPath, Size = sourceLength, Mode = verb
            });

            TryDelete(partPath);

            // ---- 3. copy, hashing the source as we read it -----------------------------------
            string sourceHash;
            if (!string.IsNullOrWhiteSpace(rule.ExternalCommand))
            {
                src.Dispose();
                src = null;
                var extResult = await RunExternalAsync(rule, sourcePath, partPath, ct).ConfigureAwait(false);
                if (extResult is not null)
                {
                    TryDelete(partPath);
                    return Fail(rule, sourcePath, targetPath, partPath, extResult, sw);
                }
                sourceHash = await HashFileAsync(sourcePath, rule.HashAlgorithm, null, ct).ConfigureAwait(false);
            }
            else
            {
                sourceHash = await CopyAsync(src!, partPath, rule, sourceLength, progress, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // ---- 5 & 6. independent read-back and comparison ---------------------------------
            var partInfo = new FileInfo(LongPath.Prefix(partPath));
            if (!partInfo.Exists)
                return Fail(rule, sourcePath, targetPath, partPath, "destination file is missing after copy", sw);

            if (partInfo.Length != sourceLength)
            {
                TryDelete(partPath);
                return Fail(rule, sourcePath, targetPath, partPath,
                    $"length mismatch: source {sourceLength} bytes, destination {partInfo.Length} bytes", sw);
            }

            var verifiedHash = sourceHash;
            if (rule.VerifyMode == VerifyMode.Hash)
            {
                progress?.Report(new TransferProgress("verify", 0, sourceLength));
                var destHash = await HashFileAsync(partPath, rule.HashAlgorithm,
                    done => progress?.Report(new TransferProgress("verify", done, sourceLength)), ct)
                    .ConfigureAwait(false);

                if (!string.Equals(destHash, sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partPath);
                    return Fail(rule, sourcePath, targetPath, partPath,
                        $"{rule.HashAlgorithm} mismatch: source {sourceHash}, destination {destHash}", sw);
                }
                verifiedHash = destHash;
            }

            // ---- 7. publish under the final name ---------------------------------------------
            File.Move(LongPath.Prefix(partPath), LongPath.Prefix(targetPath));

            try
            {
                File.SetLastWriteTimeUtc(LongPath.Prefix(targetPath), sourceWriteUtc);
                File.SetCreationTimeUtc(LongPath.Prefix(targetPath), sourceCreateUtc);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not copy timestamps onto {targetPath}: {ex.Message}", rule.Name);
            }

            // ---- 8. move mode: link first, delete second ------------------------------------
            var symlinkNote = "";
            if (rule.Mode == TransferMode.Move)
            {
                src?.Dispose();
                src = null;

                var deleteError = DeleteSourceWithOptionalLink(rule, sourcePath, targetPath, out symlinkNote);
                if (deleteError is not null)
                {
                    // The data is safe and verified at the target; only the source-side step failed.
                    _log.Error($"Data is SAFE at {targetPath}, but the source could not be finalised: {deleteError}", rule.Name);
                    _journal.Append(new JournalRecord
                    {
                        Event = "fail", Rule = rule.Name, Source = sourcePath, Target = targetPath,
                        Part = partPath, Size = sourceLength, Hash = verifiedHash, Mode = verb,
                        Detail = "copied and verified, source finalisation failed: " + deleteError
                    });
                    return new TransferOutcome(TransferStatus.Failed,
                        $"copied and verified, but source finalisation failed: {deleteError}",
                        targetPath, sourceLength, verifiedHash, sw.Elapsed);
                }

                if (rule.DeleteEmptySourceDirs) PruneEmptyDirs(rule, sourcePath);
            }

            _journal.Append(new JournalRecord
            {
                Event = "done", Rule = rule.Name, Source = sourcePath, Target = targetPath,
                Part = partPath, Size = sourceLength, Hash = verifiedHash, Mode = verb
            });

            AppendIndex(rule, sourcePath, targetPath, sourceLength, verifiedHash);

            var mbs = sw.Elapsed.TotalSeconds > 0.001
                ? sourceLength / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds
                : 0;
            _log.Info($"{verb} OK  {Path.GetFileName(sourcePath)}  ->  {targetPath}  " +
                      $"({FormatSize(sourceLength)}, {mbs:F1} MB/s, {rule.HashAlgorithm}:{verifiedHash}){symlinkNote}", rule.Name);

            return new TransferOutcome(TransferStatus.Transferred, "ok", targetPath, sourceLength, verifiedHash, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            _journal.Append(new JournalRecord
            {
                Event = "fail", Rule = rule.Name, Source = sourcePath, Target = targetPath,
                Part = partPath, Mode = verb, Detail = "cancelled"
            });
            _log.Warn($"Cancelled, source untouched: {sourcePath}", rule.Name);
            return new TransferOutcome(TransferStatus.Cancelled, "cancelled", Elapsed: sw.Elapsed);
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            return Fail(rule, sourcePath, targetPath, partPath, ex.Message, sw);
        }
        finally
        {
            src?.Dispose();
        }
    }

    // -------------------------------------------------------------------------------------

    private async Task<string> CopyAsync(
        FileStream src, string partPath, RuleConfig rule, long total,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        using var hasher = Hashing.Create(rule.HashAlgorithm);
        var chunk = Math.Max(64 * 1024, _app.CopyChunkBytes);
        var buffer = new byte[chunk];

        await using (var dst = new FileStream(
            LongPath.Prefix(partPath), FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.SequentialScan))
        {
            long done = 0;
            int read;
            src.Position = 0;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hasher.Append(buffer.AsSpan(0, read));
                done += read;
                progress?.Report(new TransferProgress("copy", done, total));
            }
            // 4. force to the storage device rather than trusting the SMB write cache.
            dst.Flush(flushToDisk: true);
        }

        return hasher.Finish();
    }

    private async Task<string> HashFileAsync(
        string path, HashKind kind, Action<long>? onProgress, CancellationToken ct)
    {
        using var hasher = Hashing.Create(kind);
        var chunk = Math.Max(64 * 1024, _app.CopyChunkBytes);
        var buffer = new byte[chunk];

        await using var fs = new FileStream(
            LongPath.Prefix(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);

        long done = 0;
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, read));
            done += read;
            onProgress?.Invoke(done);
        }
        return hasher.Finish();
    }

    /// <summary>
    /// Symlink first, delete second, rename last: a symlink failure leaves the source in place
    /// rather than leaving the original location empty. Returns null on success.
    /// </summary>
    private string? DeleteSourceWithOptionalLink(RuleConfig rule, string sourcePath, string targetPath, out string note)
    {
        note = "";
        if (!rule.CreateSymlink)
        {
            try
            {
                File.Delete(LongPath.Prefix(sourcePath));
                return null;
            }
            catch (Exception ex) { return $"could not delete source: {ex.Message}"; }
        }

        var linkTemp = sourcePath + LinkSuffix;
        TryDelete(linkTemp);

        try
        {
            CreateSymlink(linkTemp, targetPath);
        }
        catch (Exception ex)
        {
            TryDelete(linkTemp);
            return $"could not create symlink (source kept): {ex.Message}";
        }

        try
        {
            File.Delete(LongPath.Prefix(sourcePath));
        }
        catch (Exception ex)
        {
            TryDelete(linkTemp);
            return $"could not delete source: {ex.Message}";
        }

        try
        {
            File.Move(LongPath.Prefix(linkTemp), LongPath.Prefix(sourcePath));
            note = "  [symlink left at source]";
            return null;
        }
        catch (Exception ex)
        {
            return $"source deleted but the symlink could not be renamed into place " +
                   $"(it is at {linkTemp}): {ex.Message}";
        }
    }

    private void PruneEmptyDirs(RuleConfig rule, string sourcePath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.SourceFolder));
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath));

            while (!string.IsNullOrEmpty(dir) &&
                   !string.Equals(Path.TrimEndingDirectorySeparator(dir), root, StringComparison.OrdinalIgnoreCase) &&
                   dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.EnumerateFileSystemEntries(LongPath.Prefix(dir)).Any()) break;
                Directory.Delete(LongPath.Prefix(dir));
                _log.Debug($"Removed empty source folder {dir}", rule.Name);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not prune empty source folders: {ex.Message}", rule.Name);
        }
    }

    private async Task<string?> RunExternalAsync(RuleConfig rule, string src, string dst, CancellationToken ct)
    {
        var cmd = rule.ExternalCommand
            .Replace("{src}", src, StringComparison.OrdinalIgnoreCase)
            .Replace("{dst}", dst, StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _log.Debug($"External command: {cmd}", rule.Name);
        using var p = Process.Start(psi);
        if (p is null) return "could not start the external command";

        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);

        // robocopy uses exit codes 0-7 for success; everything else treats non-zero as failure.
        var ok = cmd.TrimStart().StartsWith("robocopy", StringComparison.OrdinalIgnoreCase)
            ? p.ExitCode < 8
            : p.ExitCode == 0;

        if (ok) return null;
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return $"external command exited with {p.ExitCode}: {detail.Trim()}";
    }

    private void AppendIndex(RuleConfig rule, string source, string target, long size, string hash)
    {
        if (string.IsNullOrWhiteSpace(rule.IndexFile)) return;

        var path = Path.IsPathRooted(rule.IndexFile)
            ? rule.IndexFile
            : Path.Combine(rule.TargetFolder, rule.IndexFile);

        var relative = target;
        try { relative = Path.GetRelativePath(rule.TargetFolder, target); } catch { /* keep absolute */ }

        var line = string.Join('\t',
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            rule.Name, relative, size.ToString(),
            $"{rule.HashAlgorithm.ToString().ToLowerInvariant()}:{hash}", source);

        lock (_indexGate)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var full = LongPath.Prefix(path);
                    if (!File.Exists(full))
                        File.AppendAllText(full, "timestamp\trule\ttarget\tsize\thash\tsource" + Environment.NewLine, Encoding.UTF8);
                    File.AppendAllText(full, line + Environment.NewLine, Encoding.UTF8);
                    return;
                }
                catch (IOException) { Thread.Sleep(200); }
                catch (Exception ex)
                {
                    _log.Warn($"Could not append to index file {path}: {ex.Message}", rule.Name);
                    return;
                }
            }
            _log.Warn($"Could not append to index file {path}: still locked after 3 attempts.", rule.Name);
        }
    }

    private TransferOutcome Fail(RuleConfig rule, string source, string target, string part, string message, Stopwatch sw)
    {
        _log.Error($"FAILED  {source}  ->  {target}  : {message}  (source untouched)", rule.Name);
        _journal.Append(new JournalRecord
        {
            Event = "fail", Rule = rule.Name, Source = source, Target = target,
            Part = part, Mode = rule.Mode.ToString(), Detail = message
        });
        return new TransferOutcome(TransferStatus.Failed, message, target, Elapsed: sw.Elapsed);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(LongPath.Prefix(path))) File.Delete(LongPath.Prefix(path)); }
        catch { /* best effort */ }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };
}
