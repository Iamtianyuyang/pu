using System.Text.Json;
using Pu.Core;

namespace Pu.App.Shell;

/// <summary>
/// 右键菜单扩展名清单（方案.md 第七节：清单放配置文件，随时增删）。
/// 配置文件 %LOCALAPPDATA%\Pu\extensions.json，首次运行写入默认值（与 Pu.Core 共享）。
/// </summary>
public static class ShellConfig
{
    private static readonly string[] DefaultExtensions = MediaExtensions.Defaults;

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
            // 扩展名是 .xxx 令牌，不含引号，手写 JSON 避免 AOT 反射序列化
            var json = "{\"extensions\":[" + string.Join(",", defaults.Select(s => "\"" + s + "\"")) + "]}";
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
        return defaults;
    }
}
