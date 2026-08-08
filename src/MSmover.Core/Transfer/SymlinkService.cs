using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using MSmover.Core.Common;

namespace MSmover.Core.Transfer;

public sealed record SymlinkEvaluation(bool L2L, bool L2R, bool R2L, bool R2R)
{
    public static readonly SymlinkEvaluation Unknown = new(true, false, false, false);
}

public sealed record SymlinkCapability(
    bool CanCreate,
    bool CanFollow,
    string Detail,
    bool DeveloperMode,
    bool Elevated,
    SymlinkEvaluation Evaluation,
    bool TargetIsRemote)
{
    /// <summary>Links can be created but Windows refuses to resolve them through to the target.</summary>
    public bool WillNotBeFollowed => CanCreate && !CanFollow;

    public bool Usable => CanCreate && CanFollow;
}

/// <summary>
/// Symlink creation plus the two things that actually stop it working on an instrument PC:
///
///   1. SeCreateSymbolicLinkPrivilege, which needs elevation or Developer Mode.
///   2. SMB symlink evaluation. Local-to-remote following is DISABLED by default on Windows,
///      and the target here is always a network drive, so this is the one that bites.
///
/// Checked up front by <see cref="Probe"/> so a rule refuses to start rather than discovering
/// the problem after it has already moved a file.
/// </summary>
public static class SymlinkService
{
    public static void Create(string linkPath, string targetPath)
        => File.CreateSymbolicLink(LongPath.Prefix(linkPath), targetPath);

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool IsDeveloperModeEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
            return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int v && v == 1;
        }
        catch { return false; }
    }

    public static bool IsRemotePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    public static SymlinkEvaluation QueryEvaluation()
    {
        try
        {
            var psi = new ProcessStartInfo("fsutil", "behavior query SymlinkEvaluation")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return SymlinkEvaluation.Unknown;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) return SymlinkEvaluation.Unknown;

            // Real output is "Local-to-local symbolic link evaluation is: ENABLED", so compare with
            // separators stripped rather than guessing at the exact spacing or hyphenation.
            static string Squash(string s) => s.Replace(" ", "").Replace("-", "");

            bool Enabled(string kind)
            {
                var needle = Squash(kind);
                foreach (var raw in output.Split('\n'))
                {
                    var line = Squash(raw);
                    if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                    return !line.Contains("DISABLED", StringComparison.OrdinalIgnoreCase)
                           && line.Contains("ENABLED", StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }

            return new SymlinkEvaluation(
                Enabled("Localtolocal"),
                Enabled("Localtoremote"),
                Enabled("Remotetolocal"),
                Enabled("Remotetoremote"));
        }
        catch { return SymlinkEvaluation.Unknown; }
    }

    /// <summary>The elevated command that makes local-to-remote symlinks followable.</summary>
    public const string FixCommand = "fsutil behavior set SymlinkEvaluation L2R:1 R2R:1";

    /// <summary>
    /// Creates a real symlink in <paramref name="sourceFolder"/> pointing into
    /// <paramref name="targetFolder"/> and deletes it again. Nothing else proves the
    /// combination of privilege, filesystem and share policy will actually work.
    /// </summary>
    public static SymlinkCapability Probe(string sourceFolder, string targetFolder)
    {
        var devMode = IsDeveloperModeEnabled();
        var elevated = IsElevated();
        var remote = IsRemotePath(targetFolder);
        var eval = QueryEvaluation();

        string? probeTarget = null;
        string? probeLink = null;
        try
        {
            Directory.CreateDirectory(LongPath.Prefix(sourceFolder));
            Directory.CreateDirectory(LongPath.Prefix(targetFolder));

            const string marker = "msmover symlink capability probe";
            probeTarget = Path.Combine(targetFolder, $".msmover-probe-{Guid.NewGuid():N}.tmp");
            probeLink = Path.Combine(sourceFolder, $".msmover-probe-{Guid.NewGuid():N}.lnktest");

            File.WriteAllText(LongPath.Prefix(probeTarget), marker);
            Create(probeLink, probeTarget);

            if (!FileGuard.IsReparsePoint(probeLink))
                return new SymlinkCapability(false, false, "Link was created but is not a reparse point.",
                    devMode, elevated, eval, remote);

            // Reading through the link is the authoritative test. It does not depend on parsing
            // fsutil output, and it survives non-English Windows and group policy we cannot see.
            bool canFollow;
            string detail;
            try
            {
                canFollow = File.ReadAllText(LongPath.Prefix(probeLink)) == marker;
                detail = canFollow
                    ? "Symlinks work: a link was created, read through to the target, and removed."
                    : "A link was created but reading through it did not return the target's content.";
            }
            catch (Exception ex)
            {
                canFollow = false;
                detail = $"A link was created but Windows would not follow it: {ex.Message}";
            }

            if (!canFollow && remote && !eval.L2R)
                detail += " Local-to-remote symbolic link evaluation is disabled.";

            return new SymlinkCapability(true, canFollow, detail, devMode, elevated, eval, remote);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SymlinkCapability(false, false,
                $"Access denied creating a symlink: {ex.Message}", devMode, elevated, eval, remote);
        }
        catch (IOException ex)
        {
            return new SymlinkCapability(false, false,
                $"Could not create a symlink: {ex.Message}", devMode, elevated, eval, remote);
        }
        catch (Exception ex)
        {
            return new SymlinkCapability(false, false,
                $"Symlink probe failed: {ex.Message}", devMode, elevated, eval, remote);
        }
        finally
        {
            TryDelete(probeLink);
            TryDelete(probeTarget);
        }
    }

    /// <summary>Human-readable guidance for the "fix this" button in the UI.</summary>
    public static string ExplainFailure(SymlinkCapability cap)
    {
        var lines = new List<string>();
        if (!cap.CanCreate)
        {
            lines.Add(cap.Detail);
            lines.Add("");
            lines.Add("Creating symbolic links needs SeCreateSymbolicLinkPrivilege. Either:");
            lines.Add("  - turn on Developer Mode (Settings > System > For developers), or");
            lines.Add("  - run MSmover as administrator.");
            lines.Add($"Developer Mode is currently {(cap.DeveloperMode ? "ON" : "OFF")}; " +
                      $"this process is {(cap.Elevated ? "elevated" : "not elevated")}.");
        }
        if (cap.WillNotBeFollowed)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add("The target is on a network location and local-to-remote symlink evaluation " +
                      "is disabled, so Windows will create the link but refuse to follow it.");
            lines.Add("Fix it once, from an elevated command prompt:");
            lines.Add($"    {FixCommand}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(LongPath.Prefix(path)); } catch { /* best effort */ }
    }
}
