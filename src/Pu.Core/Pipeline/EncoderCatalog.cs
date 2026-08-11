using Pu.Core.Common;

namespace Pu.Core.Pipeline;

/// <summary>
/// 硬件编码器探测。优先 NVENC → QSV → AMF → libx264 软编。
/// 探测结果来自 `ffmpeg -encoders`，避免写死机器不存在的编码器。
/// </summary>
public sealed class EncoderCatalog
{
    private static readonly string[] Preference = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];

    public IReadOnlyList<string> Available { get; }
    public string PreferredH264Encoder { get; }

    public EncoderCatalog(IEnumerable<string> available)
    {
        Available = available.Distinct(StringComparer.Ordinal).ToList();
        PreferredH264Encoder = Preference.FirstOrDefault(Available.Contains) ?? "libx264";
    }

    public static async Task<EncoderCatalog> DetectAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("ffmpeg", ["-hide_banner", "-encoders"], cancellationToken: ct);
        var found = new List<string>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            if (line.Length < 2 || line[0] != 'V') continue; // 只要视频编码器
            foreach (var name in Preference)
            {
                if (line.Contains(name, StringComparison.Ordinal)) { found.Add(name); break; }
            }
        }
        return new EncoderCatalog(found);
    }

    /// <summary>各编码器的质量参数（H.264 目标，CRF 类 23）。</summary>
    public static string[] ArgsFor(string encoder) => encoder switch
    {
        "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-cq", "23"],
        "h264_qsv"   => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23"],
        "h264_amf"   => ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "23", "-qp_p", "23"],
        _            => ["-c:v", encoder, "-preset", "veryfast", "-crf", "23"],
    };
}
