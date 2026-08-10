using System.Reflection;

namespace MSmover.App;

/// <summary>
/// Build identity, shown in the title bar and in Settings so a bug report can say which build it
/// came from. Release builds have this stamped from the git tag by the release workflow.
/// </summary>
internal static class AppInfo
{
    public static string Version { get; } = Resolve();

    public static string TitleBarText => $"MSmover {Version}";

    private static string Resolve()
    {
        var assembly = typeof(AppInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink appends "+<commit sha>"; keep the human-readable part.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "dev";
    }
}
