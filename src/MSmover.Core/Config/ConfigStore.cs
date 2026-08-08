using System.Text.Json;
using System.Text.Json.Serialization;
using MSmover.Core.Common;

namespace MSmover.Core.Config;

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        if (!File.Exists(path)) return new AppConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    /// <summary>
    /// Atomic save: write a temp file next to the target, then replace. A crash mid-write can
    /// never leave a truncated config, which would silently lose every rule.
    /// </summary>
    public static void Save(AppConfig config, string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }
}
