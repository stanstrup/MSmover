using MSmover.Core.Common;
using MSmover.Core.Config;

namespace MSmover.Core.Transfer;

public sealed record SymlinkEntry(
    string LinkPath,
    string? LinkTarget,
    bool TargetExists,
    bool PointsIntoRuleTarget,
    bool IsDirectory)
{
    public string DisplayName => Path.GetFileName(LinkPath);

    public string Describe => LinkTarget is null
        ? "(target could not be read)"
        : LinkTarget + (TargetExists ? "" : "   [BROKEN - target missing]");
}

/// <summary>
/// Finds and removes the symbolic links left behind in a source folder by move-with-link
/// transfers, for when you want the source machine's original paths cleared out again.
///
/// The safety property that matters: this only ever deletes reparse points, and it re-checks that
/// immediately before each delete. A real file can never be removed by it, and deleting a link
/// never touches the data the link points at.
/// </summary>
public static class SymlinkCleaner
{
    /// <summary>
    /// Enumerates every symbolic link (and other reparse point) under the rule's source folder.
    /// Read-only.
    /// </summary>
    public static IReadOnlyList<SymlinkEntry> Find(RuleConfig rule, bool recursive)
        => Find(rule.SourceFolder, rule.TargetFolder, recursive);

    public static IReadOnlyList<SymlinkEntry> Find(string sourceFolder, string? ruleTargetFolder, bool recursive)
    {
        var results = new List<SymlinkEntry>();
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(LongPath.Prefix(sourceFolder)))
            return results;

        string? normalisedTarget = null;
        if (!string.IsNullOrWhiteSpace(ruleTargetFolder))
        {
            try
            {
                normalisedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ruleTargetFolder))
                                   + Path.DirectorySeparatorChar;
            }
            catch (ArgumentException) { /* leave null; everything reports as "not into target" */ }
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            // A symlinked directory must be reported, not descended into.
            MatchType = MatchType.Simple
        };

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(LongPath.Prefix(sourceFolder), "*", options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var raw in entries)
        {
            var path = LongPath.Strip(raw);
            FileSystemInfo info;
            try
            {
                var attrs = File.GetAttributes(LongPath.Prefix(path));
                if ((attrs & FileAttributes.ReparsePoint) == 0) continue;

                info = (attrs & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(LongPath.Prefix(path))
                    : new FileInfo(LongPath.Prefix(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            string? linkTarget = null;
            var targetExists = false;
            try
            {
                linkTarget = info.LinkTarget;
                if (linkTarget is not null)
                    targetExists = File.Exists(LongPath.Prefix(linkTarget))
                                   || Directory.Exists(LongPath.Prefix(linkTarget));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            var intoTarget = false;
            if (linkTarget is not null && normalisedTarget is not null)
            {
                try
                {
                    var full = Path.GetFullPath(linkTarget);
                    intoTarget = full.StartsWith(normalisedTarget, StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException) { }
            }

            results.Add(new SymlinkEntry(
                path, linkTarget, targetExists, intoTarget,
                info is DirectoryInfo));
        }

        return results.OrderBy(e => e.LinkPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public sealed record CleanupResult(int Deleted, IReadOnlyList<string> Errors);

    /// <summary>
    /// Deletes the given links. Each one is re-checked for its reparse-point attribute immediately
    /// before deletion, so a path that stopped being a link between the scan and the click is
    /// skipped rather than deleted. Directory links are removed non-recursively, which removes the
    /// link only and never the directory it points at.
    /// </summary>
    public static CleanupResult Delete(IEnumerable<SymlinkEntry> entries)
    {
        var deleted = 0;
        var errors = new List<string>();

        foreach (var entry in entries)
        {
            try
            {
                if (!FileGuard.IsReparsePoint(entry.LinkPath))
                {
                    errors.Add($"{entry.LinkPath}: no longer a symbolic link, skipped.");
                    continue;
                }

                if (entry.IsDirectory)
                    Directory.Delete(LongPath.Prefix(entry.LinkPath), recursive: false);
                else
                    File.Delete(LongPath.Prefix(entry.LinkPath));

                deleted++;
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.LinkPath}: {ex.Message}");
            }
        }

        return new CleanupResult(deleted, errors);
    }
}
