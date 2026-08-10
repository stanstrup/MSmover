using System.Text.Json.Serialization;

namespace MSmover.Core.Config;

public sealed class RuleConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rule";

    /// <summary>New rules start disabled and in dry-run: arming is always a deliberate act.</summary>
    public bool Enabled { get; set; }

    public string SourceFolder { get; set; } = "";
    public string TargetFolder { get; set; } = "";
    public bool Recursive { get; set; }

    /// <summary>Matched against the file name only, not the full path.</summary>
    public string IncludeRegex { get; set; } = @"(?i)\.raw$";
    public string ExcludeRegex { get; set; } = "";

    public TransferMode Mode { get; set; } = TransferMode.Copy;

    // ---- filename -> sub-path mapping ----

    public string Delimiter { get; set; } = "_";

    /// <summary>
    /// Null disables the check. When set, a file whose base name does not contain exactly this
    /// many delimiters is skipped, with a "too few/too many delimiters" reason.
    /// </summary>
    public int? ExpectedDelimiterCount { get; set; }

    /// <summary>Relative to <see cref="TargetFolder"/>. See PathMapper for the token list.</summary>
    public string TargetTemplate { get; set; } = "{filename}";

    public DateTokenSource DateTokenSource { get; set; } = DateTokenSource.FileModified;

    // ---- completion detection ----

    public int MinAgeSeconds { get; set; } = 60;
    public int StabilityProbes { get; set; } = 3;
    public int StabilityIntervalSeconds { get; set; } = 10;
    public long MinSizeBytes { get; set; } = 1024;

    /// <summary>
    /// Optional companion-file guard, for acquisitions that only become valid once a marker file
    /// appears alongside them. Supports {basename} and {filename}, e.g. "{basename}.sld".
    /// </summary>
    public string RequireSiblingGlob { get; set; } = "";

    // ---- transfer ----

    public QueueOrder Order { get; set; } = QueueOrder.NewestFirst;
    public VerifyMode VerifyMode { get; set; } = VerifyMode.Hash;
    public HashKind HashAlgorithm { get; set; } = HashKind.XxHash64;
    public OnTargetExists OnTargetExists { get; set; } = OnTargetExists.Skip;
    public int MaxRetries { get; set; } = 5;
    public int RetryBackoffSeconds { get; set; } = 30;
    public int Parallelism { get; set; } = 1;

    /// <summary>Leave a symlink at the original location pointing at the moved file.</summary>
    public bool CreateSymlink { get; set; }

    public bool DeleteEmptySourceDirs { get; set; }

    /// <summary>Optional TSV of completed transfers, appended at the target root.</summary>
    public string IndexFile { get; set; } = "";

    /// <summary>
    /// Escape hatch: run an external copier instead of the built-in one.
    /// Placeholders {src} and {dst}. Verification still runs afterwards.
    /// </summary>
    public string ExternalCommand { get; set; } = "";

    public bool DryRun { get; set; } = true;

    public int RescanSeconds { get; set; } = 300;

    [JsonIgnore]
    public bool WillDeleteSource => Mode == TransferMode.Move && !DryRun;

    public RuleConfig Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, ConfigStore.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<RuleConfig>(json, ConfigStore.JsonOptions)!;
    }

    /// <summary>Settings-level validation. Returns the problems, empty when the rule is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Rule name is empty.");
        if (string.IsNullOrWhiteSpace(SourceFolder)) errors.Add("Source folder is empty.");
        if (string.IsNullOrWhiteSpace(TargetFolder)) errors.Add("Target folder is empty.");
        if (string.IsNullOrWhiteSpace(TargetTemplate)) errors.Add("Target template is empty.");
        if (string.IsNullOrWhiteSpace(Delimiter) && ExpectedDelimiterCount is not null)
            errors.Add("A delimiter is required when an expected delimiter count is set.");

        foreach (var (pattern, label) in new[] { (IncludeRegex, "Include regex"), (ExcludeRegex, "Exclude regex") })
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            try { _ = new System.Text.RegularExpressions.Regex(pattern); }
            catch (ArgumentException ex) { errors.Add($"{label} is invalid: {ex.Message}"); }
        }

        if (MinAgeSeconds < 0) errors.Add("Minimum age cannot be negative.");
        if (StabilityProbes < 1) errors.Add("Stability probes must be at least 1.");
        if (StabilityIntervalSeconds < 1) errors.Add("Stability interval must be at least 1 second.");
        if (Parallelism is < 1 or > 8) errors.Add("Parallelism must be between 1 and 8.");
        if (MaxRetries < 0) errors.Add("Max retries cannot be negative.");

        if (Mode == TransferMode.Move && VerifyMode == VerifyMode.None)
            errors.Add("Verification cannot be disabled in Move mode: the source would be deleted unverified.");

        if (!string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(TargetFolder))
        {
            try
            {
                var src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(SourceFolder));
                var dst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TargetFolder));
                if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Source and target folders are the same.");
                else if (dst.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Recursive)
                    errors.Add("Target is inside the source folder while recursive is on: transfers would feed themselves.");
            }
            catch (ArgumentException) { errors.Add("Source or target folder is not a valid path."); }
        }

        return errors;
    }
}
