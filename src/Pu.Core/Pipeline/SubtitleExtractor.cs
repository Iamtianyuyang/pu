using System.Text.Json;
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

    private const string MetaFileName = ".meta";

    /// <summary>单次 ffmpeg 调用的超时：字幕是尽力而为的副产物，卡死不能拖住整个会话。</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);
    /// <summary>整次抽取（单遍 + 逐条兜底）的总截止时间：防多字幕 + 坏流组合拖到无穷。</summary>
    private static readonly TimeSpan OverallDeadline = TimeSpan.FromMinutes(15);

    public static async Task<List<SubtitleFile>> ExtractAsync(
        string sourcePath, MediaInfo info, string artifactDir, CancellationToken ct = default)
    {
        var targets = info.Subtitles.Where(s => Convertible.Contains(s.Codec)).ToList();
        if (targets.Count == 0) return [];

        // 全部 VTT 已存在（缓存命中/上次已抽）且源文件未变 → 免 ffmpeg 直接复用。
        // 键与产物复用一致（路径|大小|mtime）：.meta 记录源文件身份，源被替换时旧字幕不挂到新视频上。
        if (SourceMatches(sourcePath, artifactDir)
            && targets.All(s => File.Exists(VttPathFor(artifactDir, s.Index))))
            return targets.Select(s => new SubtitleFile(s.Index, s.Codec, s.Language, s.Title,
                VttPathFor(artifactDir, s.Index))).ToList();

        Directory.CreateDirectory(Path.Combine(artifactDir, "subs"));

        // 总截止时间：单遍 + 逐条兜底共用。到点后 ffmpeg 调用被取消，取消异常向上传播，
        // 调用方按「字幕放弃」处理（视频不受影响）——挂死的抽取不能再堵住 job 定案。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(OverallDeadline);
        var dc = deadline.Token;

        var singlePass = await ExtractSinglePassAsync(sourcePath, targets, artifactDir, dc);
        if (singlePass is not null)
        {
            WriteSourceMeta(sourcePath, artifactDir);
            return singlePass;
        }

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
            }, cancellationToken: dc, timeout: CallTimeout);
            if (r.ExitCode == 0 && File.Exists(vtt))
                result.Add(new SubtitleFile(s.Index, s.Codec, s.Language, s.Title, vtt));
        }
        if (result.Count > 0) WriteSourceMeta(sourcePath, artifactDir);
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

    /// <summary>subs/.meta：记录抽取时的源文件大小 + mtime，复用前校验，防陈旧字幕挂到被替换的源文件上。</summary>
    private static bool SourceMatches(string sourcePath, string artifactDir)
    {
        try
        {
            var metaPath = Path.Combine(artifactDir, "subs", MetaFileName);
            if (!File.Exists(metaPath)) return false;
            var fi = new FileInfo(sourcePath);
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            return root.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var size) && size == fi.Length
                && root.TryGetProperty("mtime", out var mtimeEl) && mtimeEl.TryGetInt64(out var mtime)
                && mtime == fi.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteSourceMeta(string sourcePath, string artifactDir)
    {
        try
        {
            var fi = new FileInfo(sourcePath);
            var json = $"{{\"size\":{fi.Length},\"mtime\":{fi.LastWriteTimeUtc.Ticks}}}";
            File.WriteAllText(Path.Combine(artifactDir, "subs", MetaFileName), json);
        }
        catch { /* 元数据写失败只是下次重抽，不影响字幕本身 */ }
    }
}
