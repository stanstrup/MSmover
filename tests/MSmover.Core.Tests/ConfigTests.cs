using MSmover.Core.Config;
using Xunit;

namespace MSmover.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void Round_trips_through_json_with_enum_names()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "config.json");

        var config = new AppConfig { GlobalMaxConcurrentTransfers = 3 };
        config.Rules.Add(new RuleConfig
        {
            Name = "Thermo raw",
            Mode = TransferMode.Move,
            HashAlgorithm = HashKind.Sha256,
            Order = QueueOrder.OldestFirst,
            ExpectedDelimiterCount = 2,
            TargetTemplate = @"{t1}\{t1}.pro\Data\{filename}"
        });

        ConfigStore.Save(config, path);
        var text = File.ReadAllText(path);
        var loaded = ConfigStore.Load(path);

        Assert.Contains("\"Move\"", text);          // readable, not integer enums
        Assert.Equal(3, loaded.GlobalMaxConcurrentTransfers);
        Assert.Single(loaded.Rules);
        Assert.Equal(TransferMode.Move, loaded.Rules[0].Mode);
        Assert.Equal(HashKind.Sha256, loaded.Rules[0].HashAlgorithm);
        Assert.Equal(2, loaded.Rules[0].ExpectedDelimiterCount);
    }

    [Fact]
    public void Saving_over_an_existing_file_is_atomic_and_leaves_no_temp_behind()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "config.json");

        ConfigStore.Save(new AppConfig(), path);
        ConfigStore.Save(new AppConfig { GlobalMaxConcurrentTransfers = 7 }, path);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(7, ConfigStore.Load(path).GlobalMaxConcurrentTransfers);
    }

    [Fact]
    public void A_missing_file_yields_defaults_rather_than_throwing()
    {
        using var ws = new TempWorkspace();
        var loaded = ConfigStore.Load(Path.Combine(ws.Root, "nope.json"));

        Assert.Empty(loaded.Rules);
        Assert.False(loaded.GlobalDryRun);
    }

    [Fact]
    public void Move_mode_without_verification_is_rejected()
    {
        var rule = new RuleConfig
        {
            Name = "r",
            SourceFolder = @"C:\a",
            TargetFolder = @"C:\b",
            Mode = TransferMode.Move,
            VerifyMode = VerifyMode.None
        };

        Assert.Contains(rule.Validate(), e => e.Contains("Verification cannot be disabled"));
    }

    [Fact]
    public void A_target_inside_a_recursive_source_is_rejected()
    {
        var rule = new RuleConfig
        {
            Name = "r",
            SourceFolder = @"C:\data",
            TargetFolder = @"C:\data\archive",
            Recursive = true
        };

        Assert.Contains(rule.Validate(), e => e.Contains("feed themselves"));
    }

    [Fact]
    public void An_invalid_regex_is_reported_before_the_rule_can_run()
    {
        var rule = new RuleConfig
        {
            Name = "r",
            SourceFolder = @"C:\a",
            TargetFolder = @"C:\b",
            IncludeRegex = "([unclosed"
        };

        Assert.Contains(rule.Validate(), e => e.StartsWith("Include regex is invalid"));
    }

    [Fact]
    public void New_rules_default_to_disabled_and_dry_run()
    {
        var rule = new RuleConfig();
        Assert.False(rule.Enabled);
        Assert.True(rule.DryRun);
        Assert.Equal(TransferMode.Copy, rule.Mode);
        Assert.Equal(VerifyMode.Hash, rule.VerifyMode);
        Assert.Equal(QueueOrder.NewestFirst, rule.Order);
    }
}
