namespace MSmover.Core.Common;

/// <summary>
/// Win32 path-length help. A UNC target plus a template like {t1}\{t1}.pro\Data\{filename}
/// goes past MAX_PATH more easily than you would think, so every path handed to a real IO
/// call goes through <see cref="Prefix"/> first.
/// </summary>
public static class LongPath
{
    private const int Threshold = 240;

    /// <summary>
    /// Adds the \\?\ (or \\?\UNC\) prefix when a path is long enough to be at risk.
    /// Paths must already be absolute and normalised: the prefix disables normalisation,
    /// so "." and ".." segments would survive into the syscall.
    /// </summary>
    public static string Prefix(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (path.Length < Threshold) return path;
        if (!Path.IsPathFullyQualified(path)) return path;

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path.Substring(2)
            : @"\\?\" + path;
    }

    /// <summary>Strips the extended-length prefix again, for display and logging.</summary>
    public static string Strip(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path.Substring(4);
        return path;
    }
}
