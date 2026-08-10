using MSmover.Core.Transfer;
using Xunit;

namespace MSmover.Core.Tests;

public class SymlinkCleanerTests
{
    [Fact]
    public void Ordinary_files_and_folders_are_never_listed()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("REAL_A01_001.raw", 4_000);
        ws.WriteFile(Path.Combine("sub", "REAL_A01_002.raw"), 4_000);

        var found = SymlinkCleaner.Find(ws.Source, ws.Target, recursive: true);

        Assert.Empty(found);
    }

    [Fact]
    public void Delete_refuses_anything_that_is_not_a_reparse_point()
    {
        using var ws = new TempWorkspace();
        var real = ws.WriteFile("PRECIOUS_A01_001.raw", 4_000);
        var contents = File.ReadAllBytes(real);

        // A hand-made entry claiming a real file is a link. The re-check must catch it.
        var entry = new SymlinkEntry(real, @"C:\somewhere\else.raw", false, false, false);

        var result = SymlinkCleaner.Delete(new[] { entry });

        Assert.Equal(0, result.Deleted);
        Assert.Single(result.Errors);
        Assert.Contains("no longer a symbolic link", result.Errors[0]);
        Assert.True(File.Exists(real));
        Assert.Equal(contents, File.ReadAllBytes(real));
    }

    [Fact]
    public void A_missing_source_folder_yields_an_empty_list_rather_than_throwing()
    {
        var found = SymlinkCleaner.Find(@"Z:\does\not\exist", @"Z:\nor\this", recursive: true);
        Assert.Empty(found);
    }

    [Fact]
    public void Links_are_found_classified_and_removed_without_touching_their_targets()
    {
        using var ws = new TempWorkspace();
        if (!SymlinkService.Probe(ws.Source, ws.Target).Usable)
            return;   // no symlink privilege on this machine

        // One link into the rule's target, one pointing somewhere else, one broken, one real file.
        var archived = Path.Combine(ws.Target, "ARCHIVED_A01_001.raw");
        File.WriteAllBytes(archived, new byte[5_000]);
        var elsewhere = Path.Combine(ws.Root, "outside.raw");
        File.WriteAllBytes(elsewhere, new byte[1_000]);

        SymlinkService.Create(Path.Combine(ws.Source, "ARCHIVED_A01_001.raw"), archived);
        SymlinkService.Create(Path.Combine(ws.Source, "OUTSIDE_A01_001.raw"), elsewhere);
        SymlinkService.Create(Path.Combine(ws.Source, "BROKEN_A01_001.raw"), Path.Combine(ws.Target, "gone.raw"));
        var real = ws.WriteFile("REAL_A01_001.raw", 3_000);

        var found = SymlinkCleaner.Find(ws.Source, ws.Target, recursive: false);

        Assert.Equal(3, found.Count);
        Assert.DoesNotContain(found, e => e.LinkPath.Equals(real, StringComparison.OrdinalIgnoreCase));

        var intoTarget = found.Single(e => e.DisplayName == "ARCHIVED_A01_001.raw");
        Assert.True(intoTarget.PointsIntoRuleTarget);
        Assert.True(intoTarget.TargetExists);

        Assert.False(found.Single(e => e.DisplayName == "OUTSIDE_A01_001.raw").PointsIntoRuleTarget);

        var broken = found.Single(e => e.DisplayName == "BROKEN_A01_001.raw");
        Assert.True(broken.PointsIntoRuleTarget);
        Assert.False(broken.TargetExists);
        Assert.Contains("BROKEN", broken.Describe);

        var result = SymlinkCleaner.Delete(found);

        Assert.Equal(3, result.Deleted);
        Assert.Empty(result.Errors);
        Assert.Empty(SymlinkCleaner.Find(ws.Source, ws.Target, recursive: false));

        // The whole point: the archived data and the real file are untouched.
        Assert.True(File.Exists(archived));
        Assert.Equal(5_000, new FileInfo(archived).Length);
        Assert.True(File.Exists(elsewhere));
        Assert.True(File.Exists(real));
    }

    [Fact]
    public void Recursion_is_honoured()
    {
        using var ws = new TempWorkspace();
        if (!SymlinkService.Probe(ws.Source, ws.Target).Usable) return;

        var archived = Path.Combine(ws.Target, "NESTED_A01_001.raw");
        File.WriteAllBytes(archived, new byte[1_000]);
        Directory.CreateDirectory(Path.Combine(ws.Source, "2026", "week12"));
        SymlinkService.Create(Path.Combine(ws.Source, "2026", "week12", "NESTED_A01_001.raw"), archived);

        Assert.Empty(SymlinkCleaner.Find(ws.Source, ws.Target, recursive: false));
        Assert.Single(SymlinkCleaner.Find(ws.Source, ws.Target, recursive: true));
    }
}
