using System.Text;
using System.Text.RegularExpressions;
using MSmover.Core.Config;

namespace MSmover.Core.Naming;

public enum MapVerdict
{
    Ok,
    NotIncluded,
    Excluded,
    TooFewDelimiters,
    TooManyDelimiters,
    UnknownToken,
    EmptyToken,
    InvalidPath
}

public sealed class MapResult
{
    public MapVerdict Verdict { get; init; }
    public string Reason { get; init; } = "";
    public string? RelativeTarget { get; init; }
    public string? FullTarget { get; init; }
    public bool Ok => Verdict == MapVerdict.Ok;

    public static MapResult Fail(MapVerdict v, string reason) => new() { Verdict = v, Reason = reason };
}

/// <summary>
/// Turns a source file name into a target sub-path.
///
/// This is the generalisation of win_waters_mover.bat, which hardcoded
/// <c>&lt;out&gt;\%%a\%%a.pro\Data\%%~nxi</c> where %%a was token 1 of the name split on "_",
/// and rejected names that did not have exactly the expected number of delimiters.
/// The same behaviour is now the template <c>{t1}\{t1}.pro\Data\{filename}</c> with
/// ExpectedDelimiterCount = 2.
///
/// Pure string logic on purpose: the rule editor's live preview calls it with a filename the
/// user is typing, which need not exist on disk.
/// </summary>
public static class PathMapper
{
    private static readonly Regex TokenPattern = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly char[] Separators = { '\\', '/' };

    public static readonly IReadOnlyList<(string Token, string Meaning)> TokenHelp = new[]
    {
        ("{t1} ... {tN}", "base name split on the delimiter, 1-based"),
        ("{filename}",    "file name with extension"),
        ("{basename}",    "file name without extension"),
        ("{ext}",         "extension without the dot"),
        ("{relpath}",     "sub-folder below the source root (recursive mode)"),
        ("{yyyy} {yy}",   "year from the date source"),
        ("{MM} {dd}",     "month / day (case sensitive)"),
        ("{HH} {mm} {ss}","hour / minute / second (case sensitive)"),
        ("{g:name}",      "named capture group from the include regex"),
        ("{machine}",     "this computer's name"),
        ("{rulename}",    "the rule's name")
    };

    /// <param name="sourceFullPath">Absolute path of the candidate file. Need not exist.</param>
    /// <param name="stamp">Timestamp backing the date tokens.</param>
    public static MapResult Map(RuleConfig rule, string sourceFullPath, DateTime stamp)
    {
        var fileName = Path.GetFileName(sourceFullPath);
        if (string.IsNullOrEmpty(fileName))
            return MapResult.Fail(MapVerdict.InvalidPath, "Source path has no file name.");

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName).TrimStart('.');

        // --- include / exclude -------------------------------------------------
        Match? includeMatch = null;
        if (!string.IsNullOrEmpty(rule.IncludeRegex))
        {
            includeMatch = Regex.Match(fileName, rule.IncludeRegex);
            if (!includeMatch.Success)
                return MapResult.Fail(MapVerdict.NotIncluded,
                    $"Name does not match include regex /{rule.IncludeRegex}/.");
        }

        if (!string.IsNullOrEmpty(rule.ExcludeRegex) && Regex.IsMatch(fileName, rule.ExcludeRegex))
            return MapResult.Fail(MapVerdict.Excluded,
                $"Name matches exclude regex /{rule.ExcludeRegex}/.");

        // --- delimiter tokens --------------------------------------------------
        string[] tokens;
        if (!string.IsNullOrEmpty(rule.Delimiter))
        {
            tokens = baseName.Split(rule.Delimiter);
            if (rule.ExpectedDelimiterCount is int expected)
            {
                var actual = tokens.Length - 1;
                if (actual < expected)
                    return MapResult.Fail(MapVerdict.TooFewDelimiters,
                        $"Filename check: too few delimiters (found {actual}, expected {expected}). File ignored.");
                if (actual > expected)
                    return MapResult.Fail(MapVerdict.TooManyDelimiters,
                        $"Filename check: too many delimiters (found {actual}, expected {expected}). File ignored.");
            }
        }
        else
        {
            tokens = new[] { baseName };
        }

        // --- relative sub-folder below the source root -------------------------
        var relDir = "";
        try
        {
            var root = Path.GetFullPath(rule.SourceFolder);
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFullPath));
            if (dir is not null && dir.Length > root.Length &&
                dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                relDir = Path.GetRelativePath(root, dir);
                if (relDir == ".") relDir = "";
            }
        }
        catch (ArgumentException) { /* leave relDir empty */ }

        // --- expand ------------------------------------------------------------
        MapVerdict? failure = null;
        string failureReason = "";

        var expanded = TokenPattern.Replace(rule.TargetTemplate, m =>
        {
            if (failure is not null) return "";
            var name = m.Groups[1].Value;
            var value = Resolve(name, rule, tokens, fileName, baseName, ext, relDir, stamp, includeMatch,
                                out var problem, out var problemReason);
            if (problem is not null)
            {
                failure = problem;
                failureReason = problemReason;
                return "";
            }
            return value;
        });

        if (failure is not null) return MapResult.Fail(failure.Value, failureReason);

        // --- normalise and sanity check ----------------------------------------
        var segments = expanded
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        if (segments.Length == 0)
            return MapResult.Fail(MapVerdict.EmptyToken, "Target template resolved to an empty path.");

        foreach (var s in segments)
        {
            if (s is "." or "..")
                return MapResult.Fail(MapVerdict.InvalidPath, "Target template resolved to a path containing '.' or '..'.");
            if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return MapResult.Fail(MapVerdict.InvalidPath, $"Path segment '{s}' contains characters not allowed in a file name.");
            if (s.EndsWith('.') || s.EndsWith(' '))
                return MapResult.Fail(MapVerdict.InvalidPath, $"Path segment '{s}' ends with a dot or space, which Windows cannot store.");
        }

        var relative = string.Join(Path.DirectorySeparatorChar, segments);

        string full;
        try
        {
            var root = Path.GetFullPath(rule.TargetFolder);
            full = Path.GetFullPath(Path.Combine(root, relative));

            // Containment check: nothing a filename contains may escape the target root.
            var rootWithSep = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                return MapResult.Fail(MapVerdict.InvalidPath, "Resolved target escapes the target folder.");
        }
        catch (ArgumentException ex)
        {
            return MapResult.Fail(MapVerdict.InvalidPath, $"Resolved target is not a valid path: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            return MapResult.Fail(MapVerdict.InvalidPath, $"Resolved target is not a valid path: {ex.Message}");
        }

        return new MapResult { Verdict = MapVerdict.Ok, RelativeTarget = relative, FullTarget = full };
    }

    private static string Resolve(
        string name, RuleConfig rule, string[] tokens,
        string fileName, string baseName, string ext, string relDir,
        DateTime stamp, Match? includeMatch,
        out MapVerdict? problem, out string problemReason)
    {
        problem = null;
        problemReason = "";

        // {t1}..{tN}
        if ((name[0] is 't' or 'T') && name.Length > 1 && name.AsSpan(1).ToString().All(char.IsDigit))
        {
            var idx = int.Parse(name.AsSpan(1));
            if (idx < 1 || idx > tokens.Length)
            {
                problem = MapVerdict.UnknownToken;
                problemReason = $"Template uses {{{name}}} but the name splits into only {tokens.Length} token(s) on '{rule.Delimiter}'.";
                return "";
            }
            var v = tokens[idx - 1];
            if (v.Length == 0)
            {
                problem = MapVerdict.EmptyToken;
                problemReason = $"Token {{{name}}} is empty.";
            }
            return v;
        }

        // {g:name}
        if (name.StartsWith("g:", StringComparison.OrdinalIgnoreCase))
        {
            var group = name.Substring(2);
            if (includeMatch is null)
            {
                problem = MapVerdict.UnknownToken;
                problemReason = $"Template uses {{{name}}} but no include regex is configured.";
                return "";
            }
            var g = includeMatch.Groups[group];
            if (!g.Success)
            {
                problem = MapVerdict.UnknownToken;
                problemReason = $"Include regex has no capture group named '{group}' (or it did not match).";
                return "";
            }
            if (g.Value.Length == 0)
            {
                problem = MapVerdict.EmptyToken;
                problemReason = $"Capture group '{group}' matched an empty string.";
            }
            return g.Value;
        }

        // Case sensitive on purpose: {MM} is month, {mm} is minute.
        switch (name)
        {
            case "yyyy": return stamp.ToString("yyyy");
            case "yy": return stamp.ToString("yy");
            case "MM": return stamp.ToString("MM");
            case "dd": return stamp.ToString("dd");
            case "HH": return stamp.ToString("HH");
            case "mm": return stamp.ToString("mm");
            case "ss": return stamp.ToString("ss");
        }

        switch (name.ToLowerInvariant())
        {
            case "filename": return fileName;
            case "basename": return baseName;
            case "ext": return ext;
            case "relpath": return relDir;
            case "machine": return Environment.MachineName;
            case "rulename": return rule.Name;
        }

        problem = MapVerdict.UnknownToken;
        problemReason = $"Unknown template token {{{name}}}.";
        return "";
    }

    /// <summary>Expands {basename}/{filename} in a sibling-file glob such as "{basename}.sld".</summary>
    public static string ExpandSiblingGlob(string glob, string sourceFullPath)
    {
        var fileName = Path.GetFileName(sourceFullPath);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return new StringBuilder(glob)
            .Replace("{filename}", fileName)
            .Replace("{basename}", baseName)
            .ToString();
    }
}
