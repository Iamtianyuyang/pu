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
/// 转码决策矩阵（方案.md 第五节）—— 整个工具的核心。
/// OutputArgs 是放在 `-i 输入` 之后、输出路径之前的 ffmpeg 参数。
/// </summary>
public sealed record TranscodePlan(PlanKind Kind, string Explanation, string[] OutputArgs, string OutputExtension)
{
    public static TranscodePlan Create(MediaInfo info, EncoderCatalog encoders, string filePath)
        => Create(info, encoders, Mp4Boxes.IsFastStart(filePath));

    public static TranscodePlan Create(MediaInfo info, EncoderCatalog encoders, bool isFastStart)
    {
        var video = info.Video;
        if (video is null)
        {
            // 纯音频：AAC+MP4+faststart 直出，其余封装为 M4A（非 AAC 转 AAC）
            var audio = info.Audio;
            if (audio is null)
                return new TranscodePlan(PlanKind.Unsupported, "文件中没有可用的音视频流", [], "mp4");
            bool isMp4 = info.FormatName.Contains("mp4", StringComparison.OrdinalIgnoreCase);
            if (audio.Codec == "aac" && isMp4 && isFastStart)
                return new TranscodePlan(PlanKind.ServeOriginal, "AAC + MP4 已 faststart，原样直出", [], "m4a");
            var args = new List<string>
            {
                "-map", "0:a:0",
                "-c:a", audio.Codec == "aac" ? "copy" : "aac",
                "-movflags", "+faststart",
            };
            return new TranscodePlan(PlanKind.Remux,
                $"音频 {audio.Codec} 封装为 M4A（{("aac".Equals(audio.Codec, StringComparison.OrdinalIgnoreCase) ? "copy" : "转 AAC")}）",
                args.ToArray(), "m4a");
        }

        bool isMp4Family = info.FormatName.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                        || info.FormatName.Contains("mov", StringComparison.OrdinalIgnoreCase);
        bool audioIsAac = info.Audio is null || info.Audio.Codec == "aac";
        bool is8Bit = video.BitDepth <= 8;

        // ── 1. 零处理直出：H.264 8bit + MP4 + faststart + AAC ──
        if (video.Codec == "h264" && is8Bit && isMp4Family && audioIsAac && isFastStart)
            return new TranscodePlan(PlanKind.ServeOriginal, "H.264 + AAC + MP4 已 faststart，原样直出，零处理", [], "mp4");

        // ── 2. H.264 8bit：视频 copy，只补容器 / 音频 / faststart ──
        if (video.Codec == "h264" && is8Bit)
        {
            var args = BuildMaps(info);
            args.AddRange(["-c:v", "copy"]);
            AddAudio(args, info, audioIsAac);
            args.AddRange(["-movflags", "+faststart"]);
            var why = !isMp4Family
                ? "容器非 MP4，转封装为 MP4（视频 copy）"
                : !audioIsAac
                    ? $"音频 {info.Audio!.Codec} 需转 AAC，视频 copy"
                    : "moov 在文件尾部，重排为 faststart（视频 copy）";
            return new TranscodePlan(PlanKind.Remux, why, args.ToArray(), "mp4");
        }

        // ── 3. HEVC 8bit：视频 copy + tag 改 hvc1（Safari 硬性要求，否则黑屏不报错）──
        if (video.Codec == "hevc" && is8Bit)
        {
            var args = BuildMaps(info);
            args.AddRange(["-c:v", "copy", "-tag:v", "hvc1"]);
            AddAudio(args, info, audioIsAac);
            args.AddRange(["-movflags", "+faststart"]);
            return new TranscodePlan(PlanKind.Remux,
                "HEVC 8bit 视频 copy，codec tag 改 hvc1（Safari 必需）", args.ToArray(), "mp4");
        }

        // ── 4. 全转码：HEVC 10bit / Hi10P / AV1 / VP9 / 其它 ──
        var enc = encoders.PreferredH264Encoder ?? "libx264";
        var full = BuildMaps(info);
        full.AddRange(["-pix_fmt", "yuv420p"]);
        full.AddRange(EncoderCatalog.ArgsFor(enc));
        full.AddRange(["-tag:v", "avc1"]);
        AddAudio(full, info, audioIsAac);
        full.AddRange(["-movflags", "+faststart"]);
        return new TranscodePlan(PlanKind.FullTranscode,
            $"{video.Codec} {video.BitDepth}bit 全转码 → H.264（{enc}）", full.ToArray(), "mp4");
    }

    private static List<string> BuildMaps(MediaInfo info)
    {
        var args = new List<string> { "-map", "0:v:0" };
        if (info.Audio is not null) { args.Add("-map"); args.Add("0:a:0"); }
        return args;
    }

    private static void AddAudio(List<string> args, MediaInfo info, bool audioIsAac)
    {
        if (info.Audio is null) return;
        args.Add("-c:a");
        args.Add(audioIsAac ? "copy" : "aac");
    }
}
