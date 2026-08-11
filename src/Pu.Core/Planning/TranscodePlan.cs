using Pu.Core.Pipeline;
using Pu.Core.Probe;

namespace Pu.Core.Planning;

public enum PlanKind
{
    Unsupported,     // 无法处理（无视频流等）
    ServeOriginal,   // 零处理直出
    Remux,           // 视频 copy，只动容器/音频/tag
    FullTranscode,   // 必须重编码 → H.264
}

/// <summary>
/// 转码策略：Auto = 决策矩阵「最快看到视频」（能直出就直出、能 copy 就 copy、必须转才转）；
/// ForceGpu = 任何视频一律强制重编码（优先独显硬件编码器）。
/// 由 config.json 的 {"transcode":"auto"|"always"} 控制，默认 auto（见 PuConfig）。
/// </summary>
public enum TranscodePolicy
{
    Auto,
    ForceGpu,
}

/// <summary>
/// 转码决策矩阵（方案.md 第五节）—— 整个工具的核心。
/// InputArgs 是放在 `-i 输入` 之前的参数（如硬件解码 -hwaccel）。
/// OutputArgs 是放在 `-i 输入` 之后、输出路径之前的参数。
/// </summary>
public sealed record TranscodePlan(
    PlanKind Kind, string Explanation, string[] OutputArgs, string OutputExtension, string[]? InputArgs = null)
{
    public string[] EffectiveInputArgs => InputArgs ?? [];

    /// <summary>
    /// 这个文件按矩阵走是否会进入全转码分支（即需要硬件编码器目录）。
    /// 与 Create 的分支条件保持同步；直出/Remux/纯音频返回 false → 调用方可跳过编码器探测。
    /// </summary>
    public static bool RequiresEncoder(MediaInfo info, TranscodePolicy policy)
    {
        var video = info.Video;
        if (video is null) return false;              // 纯音频：只用内置 aac 编码器
        if (policy == TranscodePolicy.ForceGpu) return true;
        bool is8Bit = video.BitDepth <= 8;
        if (video.Codec == "h264" && is8Bit) return false;
        bool hevcMain10 = video.Codec == "hevc" && video.Profile.Contains("Main 10", StringComparison.OrdinalIgnoreCase);
        if (video.Codec == "hevc" && (is8Bit || hevcMain10)) return false;
        return true;
    }

    public static TranscodePlan Create(MediaInfo info, EncoderCatalog encoders, string filePath,
        TranscodePolicy policy = TranscodePolicy.Auto)
        => Create(info, encoders, Mp4Boxes.IsFastStart(filePath), policy);

    public static TranscodePlan Create(MediaInfo info, EncoderCatalog encoders, bool isFastStart,
        TranscodePolicy policy = TranscodePolicy.Auto)
    {
        var video = info.Video;
        if (video is null)
        {
            // 纯音频：AAC+MP4+faststart 直出，其余封装为 M4A（浏览器不能解的转 AAC）
            var audio = info.Audio;
            if (audio is null)
                return new TranscodePlan(PlanKind.Unsupported, "文件中没有可用的音视频流", [], "mp4");
            bool isMp4 = info.FormatName.Contains("mp4", StringComparison.OrdinalIgnoreCase);
            if (audio.Codec == "aac" && isMp4 && isFastStart)
                return new TranscodePlan(PlanKind.ServeOriginal, "AAC + MP4 已 faststart，原样直出", [], "m4a");
            bool copyable = audio.Codec is "aac" or "ac3" or "eac3";
            var args = new List<string>
            {
                "-map", "0:a:0",
                "-c:a", copyable ? "copy" : "aac",
                "-movflags", "+faststart",
            };
            return new TranscodePlan(PlanKind.Remux,
                $"音频 {audio.Codec} 封装为 M4A（{(copyable ? "copy" : "转 AAC")}）",
                args.ToArray(), "m4a");
        }

        bool isMp4Family = info.FormatName.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                        || info.FormatName.Contains("mov", StringComparison.OrdinalIgnoreCase);
        // 浏览器可直接解的音轨：AAC 与杜比 AC-3/E-AC-3（Chrome/Edge/Safari 均支持 MP4 里的 ac-3/ec-3）
        bool audioCopyable = info.Audio is null || info.Audio.Codec is "aac" or "ac3" or "eac3";
        bool is8Bit = video.BitDepth <= 8;

        // ── 0. 强制转码策略：跳过一切 copy 捷径，一律重编码（优先 GPU）──
        if (policy == TranscodePolicy.ForceGpu)
            return BuildFullTranscode(info, encoders, video, audioCopyable, forced: true);

        // ── 1. 零处理直出：H.264 8bit + MP4 + faststart + 可播音轨 ──
        if (video.Codec == "h264" && is8Bit && isMp4Family && audioCopyable && isFastStart)
            return new TranscodePlan(PlanKind.ServeOriginal, "H.264 + 可播音轨 + MP4 已 faststart，原样直出，零处理", [], "mp4");

        // ── 2. H.264 8bit：视频 copy，只补容器 / 音频 / faststart ──
        if (video.Codec == "h264" && is8Bit)
        {
            var args = BuildMaps(info);
            args.AddRange(["-c:v", "copy"]);
            AddAudio(args, info, audioCopyable);
            args.AddRange(["-movflags", MovflagsForRemux(info)]);
            var why = !isMp4Family
                ? "容器非 MP4，转封装为 MP4（视频 copy）"
                : !audioCopyable
                    ? $"音频 {info.Audio!.Codec} 需转 AAC，视频 copy"
                    : "moov 在文件尾部，重封装为 fMP4（视频 copy）";
            return new TranscodePlan(PlanKind.Remux, why, args.ToArray(), "mp4");
        }

        // ── 3. HEVC 8bit / Main10：视频 copy + tag 改 hvc1（Safari/iOS 原生支持，方案.md 第五节）──
        bool hevcMain10 = video.Codec == "hevc" && video.Profile.Contains("Main 10", StringComparison.OrdinalIgnoreCase);
        if (video.Codec == "hevc" && (is8Bit || hevcMain10))
        {
            var args = BuildMaps(info);
            args.AddRange(["-c:v", "copy", "-tag:v", "hvc1"]);
            AddAudio(args, info, audioCopyable);
            args.AddRange(["-movflags", MovflagsForRemux(info)]);
            return new TranscodePlan(PlanKind.Remux,
                $"HEVC {video.Profile} 视频 copy，codec tag 改 hvc1（Safari 必需）", args.ToArray(), "mp4");
        }

        // ── 4. 全转码：HEVC 10bit / Hi10P / AV1 / VP9 / 其它 ──
        return BuildFullTranscode(info, encoders, video, audioCopyable, forced: false);
    }

    private static TranscodePlan BuildFullTranscode(
        MediaInfo info, EncoderCatalog encoders, VideoStreamInfo video, bool audioCopyable, bool forced)
    {
        var enc = encoders.PreferredH264Encoder ?? "libx264";
        if (enc != "libx264" && (video.Width < 256 || video.Height < 144))
            enc = "libx264"; // 过小视频低于硬编最小尺寸（如 NVENC 145px），直接软编避免必然失败的尝试
        var full = BuildMaps(info);
        full.AddRange(["-pix_fmt", "yuv420p"]);
        full.AddRange(EncoderCatalog.ArgsFor(enc));
        full.AddRange(["-tag:v", "avc1"]);
        AddAudio(full, info, audioCopyable);
        full.AddRange(["-movflags", "+faststart"]);
        // 硬件编码器配硬件解码（失败时 Transcoder 自动软解回退）
        var hwaccel = EncoderCatalog.HwaccelFor(enc);
        var inputArgs = hwaccel is null ? [] : new[] { "-hwaccel", hwaccel };
        var how = enc == "libx264" ? "libx264 软编" : $"{enc} 硬编";
        var explanation = forced
            ? $"强制转码 {video.Codec} → H.264（{how}）"
            : $"{video.Codec} {video.BitDepth}bit 全转码 → H.264（{how}）";
        return new TranscodePlan(PlanKind.FullTranscode, explanation, full.ToArray(), "mp4", inputArgs);
    }

    private static List<string> BuildMaps(MediaInfo info)
    {
        var args = new List<string> { "-map", "0:v:0" };
        if (info.Audio is not null) { args.Add("-map"); args.Add("0:a:0"); }
        return args;
    }

    /// <summary>
    /// Remux 产物用分段 MP4（moov 随首分片落盘，免 faststart 二次整文件重写）。
    /// 但 E-AC-3/AC-3 不能用 empty_moov：muxer 要先解析过音帧才写得出 ec-3/ac-3 描述，
    /// 否则报 “Cannot write moov atom before EAC3 packets parsed”。
    /// </summary>
    private static string MovflagsForRemux(MediaInfo info)
        => info.Audio is { Codec: "ac3" or "eac3" }
            ? "frag_keyframe+default_base_moof"
            : "frag_keyframe+empty_moov+default_base_moof";

    private static void AddAudio(List<string> args, MediaInfo info, bool audioCopyable)
    {
        if (info.Audio is null) return;
        args.Add("-c:a");
        args.Add(audioCopyable ? "copy" : "aac");
    }
}
