using Pu.Core.Common;
using Pu.Core.Planning;

namespace Pu.Core.Pipeline;

public sealed record TranscodeProgress(double Fraction, TimeSpan Position, TimeSpan Total);

/// <summary>ffmpeg 执行：-progress pipe:1 解析 out_time_us 算进度，失败删除残片。</summary>
public static class Transcoder
{
    public static async Task TranscodeAsync(
        string input,
        TranscodePlan plan,
        string outputPath,
        long totalDurationUs,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-i", input,
        };
        args.AddRange(plan.OutputArgs);
        args.AddRange(["-progress", "pipe:1", outputPath]);

        double lastReported = -1;
        var result = await ProcessRunner.RunAsync("ffmpeg", args, onStdoutLine: line =>
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
            if (!long.TryParse(line.AsSpan("out_time_us=".Length), out var us) || totalDurationUs <= 0) return;
            var fraction = Math.Clamp((double)us / totalDurationUs, 0.0, 1.0);
            if (fraction < 1 && fraction - lastReported < 0.01) return; // 限频：1% 粒度
            lastReported = fraction;
            progress?.Report(new TranscodeProgress(
                fraction,
                TimeSpan.FromMicroseconds(us),
                TimeSpan.FromMicroseconds(totalDurationUs)));
        }, cancellationToken: ct);

        if (result.ExitCode != 0)
        {
            TryDelete(outputPath);
            throw new InvalidOperationException($"ffmpeg 失败: {Trim(result.StdErr)}");
        }

        progress?.Report(new TranscodeProgress(
            1, TimeSpan.FromMicroseconds(totalDurationUs), TimeSpan.FromMicroseconds(totalDurationUs)));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 尽力而为 */ }
    }

    private static string Trim(string s)
    {
        var t = s.Trim();
        return t.Length > 500 ? t[^500..] : t;
    }
}
