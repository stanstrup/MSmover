using MSmover.Core.Config;
using MSmover.Core.Transfer;
using Xunit;

namespace MSmover.Core.Tests;

/// <summary>
/// The move-with-symlink sequence is the only place MSmover deletes a source file while another
/// operation can still fail, so it is covered here without needing symlink privilege: the engine's
/// CreateSymlink seam is swapped for a stub.
/// </summary>
public class SymlinkOrderingTests : IDisposable
{
    private readonly Action<string, string> _original = TransferEngine.CreateSymlink;

    public void Dispose() => TransferEngine.CreateSymlink = _original;

    [Fact]
    public async Task A_symlink_that_cannot_be_created_leaves_the_source_file_in_place()
    {
        using var ws = new TempWorkspace();
        TransferEngine.CreateSymlink = (_, _) =>
            throw new UnauthorizedAccessException("A required privilege is not held by the client.");

        var src = ws.WriteFile("ORDER_A01_001.raw", 50_000);
        var expected = File.ReadAllBytes(src);
        var dst = Path.Combine(ws.Target, "ORDER_A01_001.raw");
        var rule = ws.Rule(r => { r.Mode = TransferMode.Move; r.CreateSymlink = true; });

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Failed, result.Status);
        Assert.True(File.Exists(src));                       // never deleted
        Assert.Equal(expected, File.ReadAllBytes(src));
        Assert.True(File.Exists(dst));                       // the verified copy is safe
        Assert.False(File.Exists(src + TransferEngine.LinkSuffix));
        Assert.Contains("Data is SAFE", ws.LogText());
    }

    [Fact]
    public async Task The_link_is_created_before_the_source_is_deleted()
    {
        using var ws = new TempWorkspace();

        var sourceStillPresentWhenLinking = false;
        var src = ws.WriteFile("ORDER_A01_002.raw", 50_000);
        var dst = Path.Combine(ws.Target, "ORDER_A01_002.raw");

        TransferEngine.CreateSymlink = (link, target) =>
        {
            sourceStillPresentWhenLinking = File.Exists(src);
            File.WriteAllText(link, "stand-in for a symlink -> " + target);
        };

        var rule = ws.Rule(r => { r.Mode = TransferMode.Move; r.CreateSymlink = true; });
        var result = await ws.Engine.ExecuteAsync(rule, src, dst, false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Transferred, result.Status);
        Assert.True(sourceStillPresentWhenLinking,
            "the link must be created while the source is still on disk, so a failure is recoverable");
        Assert.True(File.Exists(src));                       // the stand-in now occupies that path
        Assert.Contains("stand-in for a symlink", File.ReadAllText(src));
        Assert.False(File.Exists(src + TransferEngine.LinkSuffix));
        Assert.True(File.Exists(dst));
    }

    [Fact]
    public async Task Verification_failure_never_reaches_the_symlink_or_delete_step()
    {
        using var ws = new TempWorkspace();

        var linkAttempted = false;
        TransferEngine.CreateSymlink = (_, _) => { linkAttempted = true; };

        var src = ws.WriteFile("ORDER_A01_003.raw", 50_000);
        var dst = Path.Combine(ws.Target, "ORDER_A01_003.raw");
        var rule = ws.Rule(r =>
        {
            r.Mode = TransferMode.Move;
            r.CreateSymlink = true;
            r.ExternalCommand = "echo corrupted> \"{dst}\"";
        });

        var result = await ws.Engine.ExecuteAsync(rule, src, dst, false, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Failed, result.Status);
        Assert.False(linkAttempted);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dst));
    }
}
