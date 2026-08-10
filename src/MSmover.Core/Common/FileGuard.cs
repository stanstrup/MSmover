namespace MSmover.Core.Common;

/// <summary>
/// The primary "is this file finished?" probe.
///
/// Thermo Xcalibur holds the .raw file open for the whole acquisition, so an exclusive
/// open is the strongest single signal we have.
/// </summary>
public static class FileGuard
{
    /// <summary>
    /// Opens the file with FileShare.None. Returns null if any other process holds a handle.
    /// The caller keeps the returned stream for the duration of the copy, so nothing can
    /// append to the source once we have started reading it.
    /// </summary>
    public static FileStream? TryOpenExclusive(string path)
    {
        try
        {
            return new FileStream(
                LongPath.Prefix(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 1,
                FileOptions.SequentialScan);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Exclusive-open test that does not retain the handle.</summary>
    public static bool IsUnlocked(string path)
    {
        using var fs = TryOpenExclusive(path);
        return fs is not null;
    }

    /// <summary>
    /// True for symlinks, junctions and any other reparse point. Without this check the tool
    /// would happily re-process the symlinks it created itself, in a loop.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(LongPath.Prefix(path));
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
