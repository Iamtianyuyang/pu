using System.Text.Json;
using Pu.Core.Planning;

namespace Pu.Core.Common;

/// <summary>
/// %LOCALAPPDATA%\Pu\config.json 里的转码策略：{ "transcode": "auto" | "always" }。
/// 默认 auto —— 「最快看到视频」：能直出就直出（0 等待）、能 copy 就 copy（秒级）、
/// 必须转才转（优先独显硬编）；写 "always" 则任何视频都强制重编码（换全设备一致的 H.264）。
/// 每次提交实时读取，改配置立即生效。
/// </summary>
public static class PuConfig
{
    private static string ConfigPath => Path.Combine(FfmpegLocator.ConfigDir, "config.json");

    public static TranscodePolicy TranscodePolicy
    {
        get
        {
            try
            {
                if (!File.Exists(ConfigPath)) return TranscodePolicy.Auto;
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (!doc.RootElement.TryGetProperty("transcode", out var v) || v.ValueKind != JsonValueKind.String)
                    return TranscodePolicy.Auto;
                return string.Equals(v.GetString(), "always", StringComparison.OrdinalIgnoreCase)
                    ? TranscodePolicy.ForceGpu
                    : TranscodePolicy.Auto;
            }
            catch
            {
                return TranscodePolicy.Auto; // 配置损坏 → 回退默认
            }
        }
    }
}
