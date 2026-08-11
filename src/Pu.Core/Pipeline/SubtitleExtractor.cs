using Pu.Core.Common;
using Pu.Core.Probe;
using Pu.Core.Serving;

namespace Pu.Core.Pipeline;

/// <summary>
/// 字幕抽取：SRT / ASS / mov_text → WebVTT（方案.md 第十节）。
/// PGS / VobSub 是图形字幕，ffmpeg 转不了 WebVTT，直接跳过 —— 页面会说明。
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
        var result = new List<SubtitleFile>();
        foreach (var s in info.Subtitles)
        {
            if (!Convertible.Contains(s.Codec)) continue; // 图形字幕跳过硬转
            var vtt = Path.Combine(artifactDir, "subs", $"{s.Index}.vtt");
            Directory.CreateDirectory(Path.GetDirectoryName(vtt)!);
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
}
