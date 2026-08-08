using System.Text.RegularExpressions;
using MSmover.Core.Config;
using MSmover.Core.Naming;
using Xunit;

namespace MSmover.Core.Tests;

/// <summary>
/// Executable documentation. Every pattern and template printed in docs/cookbooks/regex.md and
/// docs/cookbooks/templates.md is asserted here, so a behaviour change breaks the build instead of
/// quietly making the published documentation wrong.
///
/// If you edit an example on this page, edit the matching example in the cookbook, and vice versa.
/// </summary>
public class DocumentationExamplesTests
{
    private static readonly DateTime Acquired = new(2026, 3, 14, 15, 9, 26);

    private static RuleConfig Rule(string template, string include = @"(?i)\.raw$",
                                   string? delimiter = "_", int? expectedDelims = null,
                                   string exclude = "", string source = @"D:\Data") => new()
    {
        Name = "docs",
        SourceFolder = source,
        TargetFolder = @"\\storage\ms\incoming",
        IncludeRegex = include,
        ExcludeRegex = exclude,
        Delimiter = delimiter ?? "",
        ExpectedDelimiterCount = expectedDelims,
        TargetTemplate = template
    };

    private static string Resolve(RuleConfig rule, string fileName)
    {
        var result = PathMapper.Map(rule, Path.Combine(rule.SourceFolder, fileName), Acquired);
        Assert.True(result.Ok, $"{fileName}: {result.Verdict} - {result.Reason}");
        return result.RelativeTarget!;
    }

    // ===================================================================== regex.md

    [Theory]                                              // "It is not anchored"
    [InlineData("QC_", "QC_A01_001.raw", true)]
    [InlineData("QC_", "MY_QC_A01_001.raw", true)]
    [InlineData("^QC_", "QC_A01_001.raw", true)]
    [InlineData("^QC_", "MY_QC_A01_001.raw", false)]
    public void Anchoring_table(string pattern, string name, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(name, pattern));

    [Theory]                                              // "'.' means any character"
    [InlineData(@".raw$", "Xraw", true)]
    [InlineData(@".raw$", "9raw", true)]
    [InlineData(@"\.raw$", "Xraw", false)]
    [InlineData(@"\.raw$", "run.raw", true)]
    public void Escaping_the_dot_table(string pattern, string name, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(name, pattern));

    [Theory]                                              // "case-sensitive unless you say otherwise"
    [InlineData(@"\.raw$", "RUN.RAW", false)]
    [InlineData(@"(?i)\.raw$", "RUN.RAW", true)]
    [InlineData(@"(?i)\.raw$", "run.raw", true)]
    public void Case_insensitivity_table(string pattern, string name, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(name, pattern));

    [Theory]                                              // "Selecting files"
    [InlineData(@"(?i)\.raw$", "MSTEST_A01_003.raw", true)]
    [InlineData(@"(?i)\.raw$", "notes.txt", false)]
    [InlineData(@"(?i)^QC_.*\.raw$", "QC_A01_001.raw", true)]
    [InlineData(@"(?i)^QC_.*\.raw$", "PLASMA_A01_001.raw", false)]
    [InlineData(@"(?i)^(PLASMA|SERUM|URINE)_.*\.raw$", "SERUM_C03_011.raw", true)]
    [InlineData(@"(?i)^(PLASMA|SERUM|URINE)_.*\.raw$", "TISSUE_C03_011.raw", false)]
    [InlineData(@"(?i)^[^_]+_[A-H]\d{2}_\d+\.raw$", "MSTEST_A01_003.raw", true)]
    [InlineData(@"(?i)^[^_]+_[A-H]\d{2}_\d+\.raw$", "MSTEST_Z99_003.raw", false)]
    [InlineData(@"^\d{8}_.*\.raw$", "20260314_PLASMA_003.raw", true)]
    [InlineData(@"^\d{8}_.*\.raw$", "PLASMA_003.raw", false)]
    [InlineData(@"(?i)\.(raw|mzML)$", "run.mzML", true)]
    [InlineData(@"(?i)\.(raw|mzML)$", "run.mzXML", false)]
    [InlineData(@"(?i)^[^_]{6,}_.*\.raw$", "PLASMA_x.raw", true)]
    [InlineData(@"(?i)^[^_]{6,}_.*\.raw$", "QC_x.raw", false)]
    public void Include_pattern_table(string pattern, string name, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(name, pattern));

    [Theory]                                              // "Excluding files"
    [InlineData(@"(?i)^(blank|wash|std)[_-]", "BLANK_A01_001.raw", true)]
    [InlineData(@"(?i)^(blank|wash|std)[_-]", "wash-01.raw", true)]
    [InlineData(@"(?i)^(blank|wash|std)[_-]", "PLASMA_A01_001.raw", false)]
    [InlineData(@"(?i)test", "MyTest_A01_001.raw", true)]
    [InlineData(@"(?i)^cond\d*_", "COND3_A01_001.raw", true)]
    [InlineData(@"(?i)_bad\.raw$", "MSTEST_A01_bad.raw", true)]
    [InlineData(@"^[~$]", "~partial.raw", true)]
    [InlineData(@"^[~$]", "partial.raw", false)]
    public void Exclude_pattern_table(string pattern, string name, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(name, pattern));

    [Theory]                                              // negative lookahead example
    [InlineData("MSTEST_A01_003.raw", true)]
    [InlineData("blank_A01_003.raw", false)]
    [InlineData("wash_A01_003.raw", false)]
    public void Negative_lookahead_example(string name, bool expected)
        => Assert.Equal(expected,
            Regex.IsMatch(name, @"(?i)^(?!blank|wash|std)[^_]+_[A-H]\d{2}_\d+\.raw$"));

    [Fact]                                                // "Project / plate / injection"
    public void Capture_groups_project_plate_injection()
    {
        var rule = Rule(@"{g:proj}\{g:plate}\{filename}",
            include: @"(?i)^(?<proj>[^_]+)_(?<plate>[A-H]\d{2})_(?<inj>\d+)\.raw$");

        Assert.Equal(@"PLASMA\C03\PLASMA_C03_011.raw", Resolve(rule, "PLASMA_C03_011.raw"));
    }

    [Fact]                                                // "A date embedded in the file name"
    public void Capture_groups_split_date()
    {
        var rule = Rule(@"{g:y}\{g:m}\{g:d}\{g:proj}\{filename}",
            include: @"^(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})_(?<proj>[^_]+)_.*\.raw$");

        Assert.Equal(@"2026\03\14\PLASMA\20260314_PLASMA_003.raw",
            Resolve(rule, "20260314_PLASMA_003.raw"));
    }

    [Theory]                                              // "Optional segments"
    [InlineData("LIPID_POS_004.raw", @"LIPID\POS\LIPID_POS_004.raw")]
    [InlineData("LIPID_NEG_004.raw", @"LIPID\NEG\LIPID_NEG_004.raw")]
    public void Capture_groups_alternation(string name, string expected)
    {
        var rule = Rule(@"{g:proj}\{g:mode}\{filename}",
            include: @"(?i)^(?<proj>[^_]+)_(?<mode>POS|NEG)_(?<inj>\d+)\.raw$");

        Assert.Equal(expected, Resolve(rule, name));
    }

    [Fact]                                                // "Numbered groups work too"
    public void Numbered_capture_groups_are_addressable_by_position()
    {
        var byNumber = Rule(@"{g:1}\{filename}", include: @"(?i)^([^_]+)_.*\.raw$");
        Assert.Equal(@"MSTEST\MSTEST_A01_003.raw", Resolve(byNumber, "MSTEST_A01_003.raw"));

        var byName = Rule(@"{g:proj}\{filename}", include: @"(?i)^(?<proj>[^_]+)_.*\.raw$");
        Assert.Equal(@"MSTEST\MSTEST_A01_003.raw", Resolve(byName, "MSTEST_A01_003.raw"));
    }

    [Fact]                                                // "reported as UnknownToken"
    public void A_capture_group_that_does_not_exist_is_reported()
    {
        var rule = Rule(@"{g:nope}\{filename}", include: @"(?i)^(?<proj>[^_]+)_.*\.raw$");
        var result = PathMapper.Map(rule, @"D:\Data\MSTEST_A01_003.raw", Acquired);

        Assert.Equal(MapVerdict.UnknownToken, result.Verdict);
    }

    [Fact]                                                // "a file missing from the queue entirely"
    public void Include_failure_and_naming_failure_are_distinguishable()
    {
        var rule = Rule(@"{t1}\{filename}", expectedDelims: 2);

        Assert.Equal(MapVerdict.NotIncluded,
            PathMapper.Map(rule, @"D:\Data\notes.txt", Acquired).Verdict);
        Assert.Equal(MapVerdict.TooFewDelimiters,
            PathMapper.Map(rule, @"D:\Data\MSTEST_A01.raw", Acquired).Verdict);
    }

    [Fact]                                                // "Exclude wins over include"
    public void Exclude_beats_include()
    {
        var rule = Rule(@"{filename}", exclude: @"(?i)^blank");
        Assert.Equal(MapVerdict.Excluded,
            PathMapper.Map(rule, @"D:\Data\BLANK_A01_001.raw", Acquired).Verdict);
    }

    [Fact]                                                // "In config.json, backslashes are doubled"
    public void Regexes_are_json_escaped_in_the_config_file()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "config.json");

        var config = new AppConfig();
        config.Rules.Add(new RuleConfig { IncludeRegex = @"(?i)\.raw$" });
        ConfigStore.Save(config, path);

        Assert.Contains(@"""IncludeRegex"": ""(?i)\\.raw$""", File.ReadAllText(path));
        Assert.Equal(@"(?i)\.raw$", ConfigStore.Load(path).Rules[0].IncludeRegex);
    }

    // ================================================================= templates.md

    [Theory]                                              // the "Recipes" section, in order
    [InlineData(@"{filename}", "MSTEST_A01_003.raw")]
    [InlineData(@"{t1}\{t1}.pro\Data\{filename}", @"MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw")]
    [InlineData(@"{t1}\{filename}", @"MSTEST\MSTEST_A01_003.raw")]
    [InlineData(@"{yyyy}\{MM}\{dd}\{filename}", @"2026\03\14\MSTEST_A01_003.raw")]
    [InlineData(@"{t1}\{yyyy}-{MM}\{filename}", @"MSTEST\2026-03\MSTEST_A01_003.raw")]
    [InlineData(@"{yyyy}\{yyyy}-{MM}\{t1}\{filename}", @"2026\2026-03\MSTEST\MSTEST_A01_003.raw")]
    [InlineData(@"{t1}\{t1}_{t2}_{yyyy}{MM}{dd}.{ext}", @"MSTEST\MSTEST_A01_20260314.raw")]
    public void Template_recipes(string template, string expected)
        => Assert.Equal(expected, Resolve(Rule(template), "MSTEST_A01_003.raw"));

    [Fact]                                                // "{MM} is month, {mm} is minute"
    public void Month_and_minute_are_different_tokens()
    {
        Assert.Equal(@"03\09", Resolve(Rule(@"{MM}\{mm}"), "MSTEST_A01_003.raw"));
    }

    [Theory]                                              // "Mirror the source folder structure"
    [InlineData(@"D:\Data\a.raw", "a.raw")]
    [InlineData(@"D:\Data\2026\week12\a.raw", @"2026\week12\a.raw")]
    public void Relpath_mirrors_the_source_tree(string fullPath, string expected)
    {
        var rule = Rule(@"{relpath}\{filename}");
        rule.Recursive = true;

        var result = PathMapper.Map(rule, fullPath, Acquired);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(expected, result.RelativeTarget);
    }

    [Fact]                                                // "Separate by instrument"
    public void Machine_token_uses_the_host_name()
    {
        var expected = $@"{Environment.MachineName}\MSTEST\MSTEST_A01_003.raw";
        Assert.Equal(expected, Resolve(Rule(@"{machine}\{t1}\{filename}"), "MSTEST_A01_003.raw"));
    }

    [Fact]                                                // "The delimiter split"
    public void Delimiter_split_produces_the_documented_tokens()
    {
        Assert.Equal("MSTEST", Resolve(Rule("{t1}"), "MSTEST_A01_003.raw"));
        Assert.Equal("A01", Resolve(Rule("{t2}"), "MSTEST_A01_003.raw"));
        Assert.Equal("003", Resolve(Rule("{t3}"), "MSTEST_A01_003.raw"));
    }

    [Theory]                                              // the delimiter-count table
    [InlineData("MSTEST_A01_003.raw", MapVerdict.Ok, "")]
    [InlineData("MSTEST_A01.raw", MapVerdict.TooFewDelimiters,
        "Filename check: too few delimiters (found 1, expected 2). File ignored.")]
    [InlineData("TOO_MANY_PARTS_HERE_X.raw", MapVerdict.TooManyDelimiters,
        "Filename check: too many delimiters (found 4, expected 2). File ignored.")]
    public void Delimiter_count_table(string name, MapVerdict verdict, string message)
    {
        var rule = Rule(@"{t1}\{t1}.pro\Data\{filename}", expectedDelims: 2);
        var result = PathMapper.Map(rule, Path.Combine(rule.SourceFolder, name), Acquired);

        Assert.Equal(verdict, result.Verdict);
        if (message.Length > 0) Assert.Equal(message, result.Reason);
    }

    [Theory]                                              // "What gets rejected, and why"
    [InlineData("notes.txt", @"{filename}", MapVerdict.NotIncluded)]
    [InlineData(@"{nonsense}\{filename}", null, MapVerdict.UnknownToken)]
    public void Rejection_table_simple(string a, string? b, MapVerdict expected)
    {
        // The first inline case varies the file name, the second varies the template.
        var (name, template) = b is null ? ("MSTEST_A01_003.raw", a) : (a, b);
        var rule = Rule(template);

        Assert.Equal(expected,
            PathMapper.Map(rule, Path.Combine(rule.SourceFolder, name), Acquired).Verdict);
    }

    [Fact]
    public void Rejection_table_token_out_of_range()
    {
        var rule = Rule(@"{t9}\{filename}");
        Assert.Equal(MapVerdict.UnknownToken,
            PathMapper.Map(rule, @"D:\Data\MSTEST_A01_003.raw", Acquired).Verdict);
    }

    [Fact]
    public void Rejection_table_empty_token()
    {
        var rule = Rule(@"{t1}\{filename}");
        Assert.Equal(MapVerdict.EmptyToken,
            PathMapper.Map(rule, @"D:\Data\_A_B.raw", Acquired).Verdict);
    }

    [Fact]
    public void Rejection_table_invalid_path_segment()
    {
        var rule = Rule(@"{t1}\{filename}", expectedDelims: 2);
        var result = PathMapper.Map(rule, @"D:\Data\MSTEST._A01_003.raw", Acquired);

        Assert.Equal(MapVerdict.InvalidPath, result.Verdict);
        Assert.Contains("MSTEST.", result.Reason);
    }

    [Fact]                                                // "a name that tries to climb out"
    public void A_name_cannot_climb_out_of_the_target_folder()
    {
        var rule = Rule(@"{basename}\{filename}", delimiter: null);
        var result = PathMapper.Map(rule, @"D:\Data\...raw", Acquired);

        Assert.Equal(MapVerdict.InvalidPath, result.Verdict);
    }

    [Fact]                                                // "Both \ and / work as separators"
    public void Forward_slashes_are_accepted_in_templates()
        => Assert.Equal(@"MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw",
            Resolve(Rule("{t1}/{t1}.pro/Data/{filename}"), "MSTEST_A01_003.raw"));
}
