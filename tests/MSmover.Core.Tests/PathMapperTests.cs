using MSmover.Core.Config;
using MSmover.Core.Naming;
using Xunit;

namespace MSmover.Core.Tests;

/// <summary>
/// The old win_waters_mover.bat behaviour, plus the generalisations, plus the ways a template
/// could be turned into a path that escapes the target folder.
/// </summary>
public class PathMapperTests
{
    private static RuleConfig WatersRule() => new()
    {
        Name = "waters",
        SourceFolder = @"C:\data\in",
        TargetFolder = @"C:\data\out",
        Delimiter = "_",
        ExpectedDelimiterCount = 2,
        TargetTemplate = @"{t1}\{t1}.pro\Data\{filename}",
        IncludeRegex = @"(?i)\.raw$"
    };

    private static readonly DateTime Stamp = new(2026, 3, 14, 15, 9, 26);

    [Fact]
    public void Reproduces_the_old_scripts_waters_layout()
    {
        var r = PathMapper.Map(WatersRule(), @"C:\data\in\MSTEST_A01_003.raw", Stamp);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(@"MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw", r.RelativeTarget);
        Assert.Equal(@"C:\data\out\MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw", r.FullTarget);
    }

    [Fact]
    public void Rejects_too_few_delimiters()
    {
        var r = PathMapper.Map(WatersRule(), @"C:\data\in\MSTEST_A01.raw", Stamp);

        Assert.Equal(MapVerdict.TooFewDelimiters, r.Verdict);
        Assert.Contains("too few delimiters", r.Reason);
    }

    [Fact]
    public void Rejects_too_many_delimiters()
    {
        var r = PathMapper.Map(WatersRule(), @"C:\data\in\MSTEST_A01_003_extra.raw", Stamp);

        Assert.Equal(MapVerdict.TooManyDelimiters, r.Verdict);
        Assert.Contains("too many delimiters", r.Reason);
    }

    [Fact]
    public void Delimiter_check_is_skipped_when_no_count_is_configured()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;

        var r = PathMapper.Map(rule, @"C:\data\in\MSTEST_A_B_C_D.raw", Stamp);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(@"MSTEST\MSTEST.pro\Data\MSTEST_A_B_C_D.raw", r.RelativeTarget);
    }

    [Fact]
    public void Non_matching_extension_is_not_included()
    {
        var r = PathMapper.Map(WatersRule(), @"C:\data\in\MSTEST_A01_003.txt", Stamp);
        Assert.Equal(MapVerdict.NotIncluded, r.Verdict);
    }

    [Fact]
    public void Exclude_regex_wins_over_include()
    {
        var rule = WatersRule();
        rule.ExcludeRegex = "(?i)^blank";

        var r = PathMapper.Map(rule, @"C:\data\in\BLANK_A01_003.raw", Stamp);
        Assert.Equal(MapVerdict.Excluded, r.Verdict);
    }

    [Fact]
    public void Date_tokens_are_case_sensitive_month_versus_minute()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.TargetTemplate = @"{yyyy}\{MM}\{dd}\{HH}{mm}\{filename}";

        var r = PathMapper.Map(rule, @"C:\data\in\x.raw", Stamp);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(@"2026\03\14\1509\x.raw", r.RelativeTarget);
    }

    [Fact]
    public void Named_capture_groups_resolve()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.IncludeRegex = @"(?i)^(?<proj>[^_]+)_(?<plate>[^_]+)_(?<well>\d+)\.raw$";
        rule.TargetTemplate = @"{g:proj}\{g:plate}\{filename}";

        var r = PathMapper.Map(rule, @"C:\data\in\MSTEST_A01_003.raw", Stamp);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(@"MSTEST\A01\MSTEST_A01_003.raw", r.RelativeTarget);
    }

    [Fact]
    public void Missing_capture_group_is_reported_not_silently_dropped()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.TargetTemplate = @"{g:nope}\{filename}";

        var r = PathMapper.Map(rule, @"C:\data\in\MSTEST_A01_003.raw", Stamp);

        Assert.Equal(MapVerdict.UnknownToken, r.Verdict);
    }

    [Fact]
    public void Token_index_beyond_the_split_is_reported()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.TargetTemplate = @"{t9}\{filename}";

        var r = PathMapper.Map(rule, @"C:\data\in\A_B.raw", Stamp);

        Assert.Equal(MapVerdict.UnknownToken, r.Verdict);
        Assert.Contains("only 2 token", r.Reason);
    }

    [Fact]
    public void Unknown_token_is_reported()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.TargetTemplate = @"{nonsense}\{filename}";

        var r = PathMapper.Map(rule, @"C:\data\in\A_B.raw", Stamp);
        Assert.Equal(MapVerdict.UnknownToken, r.Verdict);
    }

    [Fact]
    public void Relpath_is_empty_at_the_source_root_and_collapses_cleanly()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.Recursive = true;
        rule.TargetTemplate = @"{relpath}\{filename}";

        var atRoot = PathMapper.Map(rule, @"C:\data\in\a.raw", Stamp);
        Assert.True(atRoot.Ok, atRoot.Reason);
        Assert.Equal("a.raw", atRoot.RelativeTarget);

        var nested = PathMapper.Map(rule, @"C:\data\in\2026\week12\a.raw", Stamp);
        Assert.True(nested.Ok, nested.Reason);
        Assert.Equal(@"2026\week12\a.raw", nested.RelativeTarget);
    }

    [Fact]
    public void A_filename_cannot_escape_the_target_folder()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.Delimiter = "";
        rule.TargetTemplate = @"{basename}\{filename}";

        // A base name of ".." would otherwise resolve to the target folder's parent.
        var r = PathMapper.Map(rule, @"C:\data\in\...raw", Stamp);

        Assert.False(r.Ok);
        Assert.Equal(MapVerdict.InvalidPath, r.Verdict);
    }

    [Fact]
    public void Segments_ending_in_a_dot_are_rejected_because_windows_cannot_store_them()
    {
        var rule = WatersRule();
        rule.TargetTemplate = @"{t1}\{filename}";

        // Token 1 is "MSTEST.", which Windows cannot store as a folder name.
        var r = PathMapper.Map(rule, @"C:\data\in\MSTEST._A01_003.raw", Stamp);

        Assert.Equal(MapVerdict.InvalidPath, r.Verdict);
        Assert.Contains("ends with a dot or space", r.Reason);
    }

    [Fact]
    public void Empty_token_is_rejected_rather_than_producing_a_stray_folder()
    {
        var rule = WatersRule();
        rule.ExpectedDelimiterCount = null;
        rule.TargetTemplate = @"{t1}\{filename}";

        var r = PathMapper.Map(rule, @"C:\data\in\_A_B.raw", Stamp);

        Assert.Equal(MapVerdict.EmptyToken, r.Verdict);
    }

    [Fact]
    public void Forward_and_back_slashes_in_a_template_are_equivalent()
    {
        var rule = WatersRule();
        rule.TargetTemplate = "{t1}/{t1}.pro/Data/{filename}";

        var r = PathMapper.Map(rule, @"C:\data\in\MSTEST_A01_003.raw", Stamp);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(@"MSTEST\MSTEST.pro\Data\MSTEST_A01_003.raw", r.RelativeTarget);
    }

    [Theory]
    [InlineData("{filename}", "MSTEST_A01_003.raw")]
    [InlineData("{basename}", "MSTEST_A01_003")]
    [InlineData("{ext}", "raw")]
    [InlineData("{t2}", "A01")]
    [InlineData("{t3}", "003")]
    public void Simple_tokens(string template, string expected)
    {
        var rule = WatersRule();
        rule.TargetTemplate = template;

        var r = PathMapper.Map(rule, @"C:\data\in\MSTEST_A01_003.raw", Stamp);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(expected, r.RelativeTarget);
    }
}
