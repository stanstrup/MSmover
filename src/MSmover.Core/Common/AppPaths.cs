namespace MSmover.Core.Common;

public static class AppPaths
{
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MSmover");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string JournalFile => Path.Combine(Root, "journal.jsonl");
    public static string LogDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
