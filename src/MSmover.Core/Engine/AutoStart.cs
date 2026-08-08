using Microsoft.Win32;

namespace MSmover.Core.Engine;

/// <summary>
/// Per-user autostart via HKCU\...\Run. No admin needed, and it starts inside the logged-in
/// session, which matters: a mapped drive letter only exists in a user session, and symlink
/// creation uses that user's privileges.
/// </summary>
public static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MSmover";

    public static bool IsEnabled(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var value = key?.GetValue(ValueName) as string;
            return value is not null &&
                   value.Contains(exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key is null) return;

        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --tray");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
