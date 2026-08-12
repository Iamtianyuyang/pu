using Pu.Core.Common;

namespace Pu.Core.Pipeline;

/// <summary>
/// 硬件编码器探测。优先 NVENC → AMF → QSV → libx264 软编：
/// N 卡必为独显；AMF 多为 A 卡独显（APU 核显亦有）；QSV 基本是 Intel 核显（Arc 独显罕见），
/// 所以按这个顺序，有独显时自然优先命中独显。
/// 注意 `ffmpeg -encoders` 列的是编译进 build 的编码器（没有对应硬件也照样列），
/// 因此硬件候选逐个做 lavfi 实测，第一个真能编码的胜出。
/// </summary>
public sealed class EncoderCatalog
{
    private static readonly string[] Preference = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];
    private static readonly string[] Hardware = ["h264_nvenc", "h264_amf", "h264_qsv"];

    public IReadOnlyList<string> Available { get; }
    public string PreferredH264Encoder { get; }
    public string DetectionNote { get; }

    /// <summary>不走全转码分支时使用的占位目录（只含软编，矩阵不会读到它）。</summary>
    public static EncoderCatalog SoftwareOnly { get; } = new(["libx264"]);

    public EncoderCatalog(IEnumerable<string> available, string? detectionNote = null)
    {
        Available = available.Distinct(StringComparer.Ordinal).ToList();
        PreferredH264Encoder = Preference.FirstOrDefault(Available.Contains) ?? "libx264";
        DetectionNote = detectionNote ?? PreferredH264Encoder;
    }

    public static async Task<EncoderCatalog> DetectAsync(CancellationToken ct = default)
    {
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(FfmpegLocator.Exe, ["-hide_banner", "-encoders"],
                cancellationToken: ct, timeout: TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            // 探测卡死（罕见）：按无硬件可用处理，保证至少能软编
            return new EncoderCatalog(["libx264"], "编码器探测超时，回退 libx264 软编");
        }
        var found = new List<string>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.Length < 2 || t[0] != 'V') continue; // 只要视频编码器（行首有空格）
            foreach (var name in Preference)
            {
                if (t.Contains(name, StringComparison.Ordinal)) { found.Add(name); break; }
            }
        }

        // 实测：列表里有 ≠ 这台机器能跑（典型：没有 Intel 核显却列着 h264_qsv）。
        // 候选并发试编（NVENC/AMF/QSV 在不同硬件上，互不干扰），总耗时 ≈ 最慢的一次而非累加。
        var candidates = Hardware.Where(found.Contains).ToList();
        var results = await Task.WhenAll(
            candidates.Select(async enc => (enc, ok: await TestEncodeAsync(enc, ct))));
        var usable = results.Where(r => r.ok).Select(r => r.enc).ToList(); // 顺序保持 Preference
        var preferred = usable.FirstOrDefault() ?? "libx264";
        var note = preferred == "libx264"
            ? "libx264 软编（硬件编码器实测均不可用）"
            : $"{preferred} 硬编（实测可用，独显优先 NVENC→AMF→QSV）";
        return new EncoderCatalog(usable.Append("libx264"), note);
    }

    /// <summary>lavfi 试编超时：驱动卡死的试编会挂起整个探测，15 秒内不产出就视为不可用。</summary>
    private static readonly TimeSpan TestEncodeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>用 lavfi 生成 8 帧小视频试编，验证编码器在这台机器上真的可用（硬件/驱动都在）。</summary>
    private static async Task<bool> TestEncodeAsync(string encoder, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(FfmpegLocator.Exe,
                ["-hide_banner", "-loglevel", "error",
                 "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30",
                 "-frames:v", "8", "-pix_fmt", "yuv420p",
                 "-c:v", encoder, "-f", "null", "-"],
                cancellationToken: ct, timeout: TestEncodeTimeout);
            return result.ExitCode == 0;
        }
        catch
        {
            return false; // 含 TimeoutException：驱动卡死按不可用处理
        }
    }

    /// <summary>各编码器的质量参数（H.264 目标，CRF 类 23；预设实测调优过：NVENC p4 比 p5 快 ~13% 且文件略小，AMF speed 快 ~7%）。</summary>
    public static string[] ArgsFor(string encoder) => encoder switch
    {
        "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23"],
        "h264_qsv"   => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23"],
        "h264_amf"   => ["-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "23", "-qp_p", "23"],
        _            => ["-c:v", encoder, "-preset", "veryfast", "-crf", "23"],
    };

    /// <summary>硬件编码器对应的解码加速（输入选项，放在 -i 之前）；软编无。</summary>
    public static string? HwaccelFor(string encoder) => encoder switch
    {
        "h264_nvenc" => "cuda",
        "h264_qsv"   => "qsv",
        "h264_amf"   => "d3d11va",
        _            => null,
    };
}
