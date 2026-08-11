using System.Text.Json;

namespace Pu.Core.Common;

/// <summary>
/// ffmpeg / ffprobe 定位（方案.md 第二节：PATH → 配置文件 → 引导下载）。
/// 顺序：配置文件 → exe 旁的 ffmpeg\ 子目录（全自带版）→ PATH。
/// 配置文件：%LOCALAPPDATA%\Pu\config.json  { "ffmpeg": "D:\\ffmpeg\\bin\\ffmpeg.exe" }
/// （目录路径也行，会自动补 ffmpeg.exe；测试可用 PU_CONFIG_DIR 覆盖）
/// </summary>
public static class FfmpegLocator
{
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("PU_CONFIG_DIR") is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static string MissingGuidance =>
        "未找到 ffmpeg/ffprobe。下载：https://www.gyan.dev/ffmpeg/builds/ 解压后把 bin 目录加入 PATH，"
        + "或在 " + ConfigPath + " 中配置 {\"ffmpeg\":\"D:\\ffmpeg\\bin\\ffmpeg.exe\"}。";

    public static string Exe => ResolveFromConfig() ?? FindBundled() ?? FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg")
        ?? throw new InvalidOperationException(MissingGuidance);

    /// <summary>全自带版：pu.exe 旁的 ffmpeg\ 子目录。</summary>
    private static string? FindBundled()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        return File.Exists(p) ? p : null;
    }

    public static string ProbeExe
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Exe)!;
            var sibling = Path.Combine(exeDir, "ffprobe.exe");
            if (File.Exists(sibling)) return sibling;
            return FindOnPath("ffprobe.exe") ?? FindOnPath("ffprobe")
                ?? throw new InvalidOperationException(MissingGuidance);
        }
    }

    private static string? ResolveFromConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (!doc.RootElement.TryGetProperty("ffmpeg", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var p = v.GetString();
            if (string.IsNullOrWhiteSpace(p)) return null;
            if (Directory.Exists(p)) p = Path.Combine(p, "ffmpeg.exe");
            return File.Exists(p) ? p : null;
        }
        catch
        {
            return null; // 配置损坏 → 退回 PATH
        }
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var p = Path.Combine(dir.Trim(), name);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
