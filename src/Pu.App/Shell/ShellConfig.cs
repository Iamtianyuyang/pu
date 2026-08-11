using System.Text.Json;

namespace Pu.App.Shell;

/// <summary>
/// 右键菜单扩展名清单（方案.md 第七节：清单放配置文件，随时增删）。
/// 配置文件 %LOCALAPPDATA%\Pu\extensions.json，首次运行写入默认值。
/// </summary>
public static class ShellConfig
{
    private static readonly string[] DefaultExtensions =
    [
        // 视频
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".vob", ".3gp", ".3g2",
        ".ogv", ".rm", ".rmvb", ".asf", ".f4v", ".divx", ".hevc", ".m2v",
        // 音频
        ".mp3", ".aac", ".m4a", ".flac", ".wav", ".ogg", ".opus", ".wma", ".ac3", ".dts",
    ];

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu", "extensions.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (doc.RootElement.TryGetProperty("extensions", out var arr))
                {
                    var list = arr.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (list.Count > 0) return list;
                }
            }
        }
        catch { /* 配置损坏 → 用默认 */ }

        var defaults = DefaultExtensions.ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            // 手写 JSON：避免 AOT 下对匿名类型的反射序列化
            var json = "{\"extensions\":[" + string.Join(",", defaults.Select(s => JsonSerializer.Serialize(s))) + "]}";
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
        return defaults;
    }
}
