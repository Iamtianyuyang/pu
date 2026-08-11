using Pu.Core.Common;
using Pu.Core.Probe;
using Pu.Core.Serving;

namespace Pu.Core.Pipeline;

/// <summary>
/// 字幕抽取：SRT / ASS / mov_text → WebVTT（方案.md 第十节）。
/// PGS / VobSub 是图形字幕，ffmpeg 转不了 WebVTT，直接跳过 —— 页面会说明。
/// 单遍抽取：一次 ffmpeg 调用输出全部字幕；N 条字幕若逐条抽就是 N 次全文件扫描
/// （12 条 subrip 的 2.6GB 文件曾把「正在准备」拖成一分半钟）。单遍失败回退逐条。
/// </summary>
public static class SubtitleExtractor
{
    private static readonly HashSet<string> Convertible = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "text",
    };

    public static async Task<List<SubtitleFile>> ExtractAsync(
        string sourcePath, MediaInfo info, string artifactDir, CancellationToken ct = default)
    {
        var targets = info.Subtitles.Where(s => Convertible.Contains(s.Codec)).ToList();
        if (targets.Count == 0) return [];

        Directory.CreateDirectory(Path.Combine(artifactDir, "subs"));

        var singlePass = await ExtractSinglePassAsync(sourcePath, targets, artifactDir, ct);
        if (singlePass is not null) return singlePass;

        // 兜底：逐条抽取（某条流损坏导致整批失败时，保住能转的）
        var result = new List<SubtitleFile>();
        foreach (var s in targets)
        {
            var vtt = VttPathFor(artifactDir, s.Index);
            var r = await ProcessRunner.RunAsync(FfmpegLocator.Exe, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", sourcePath,
                "-map", $"0:{s.Index}", // 绝对流序号
                "-c:s", "webvtt",
                vtt,
            }, cancellationToken: ct);
            if (r.ExitCode == 0 && File.Exists(vtt))
                result.Add(new SubtitleFile(s.Index, s.Codec, s.Language, s.Title, vtt));
        }
        return result;
    }

    /// <summary>一次 ffmpeg 调用抽全部可转字幕；整体失败返回 null（交由逐条兜底）。</summary>
    private static async Task<List<SubtitleFile>?> ExtractSinglePassAsync(
        string sourcePath, List<SubtitleStreamInfo> targets, string artifactDir, CancellationToken ct)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", sourcePath };
        foreach (var s in targets)
            args.AddRange(["-map", $"0:{s.Index}", "-c:s", "webvtt", VttPathFor(artifactDir, s.Index)]);

        var r = await ProcessRunner.RunAsync(FfmpegLocator.Exe, args, cancellationToken: ct);
        if (r.ExitCode != 0) return null;

        var result = new List<SubtitleFile>();
        foreach (var s in targets)
        {
            var vtt = VttPathFor(artifactDir, s.Index);
            if (File.Exists(vtt))
                result.Add(new SubtitleFile(s.Index, s.Codec, s.Language, s.Title, vtt));
        }
        return result.Count > 0 ? result : null;
    }

    private static string VttPathFor(string artifactDir, int streamIndex)
        => Path.Combine(artifactDir, "subs", $"{streamIndex}.vtt");
}
