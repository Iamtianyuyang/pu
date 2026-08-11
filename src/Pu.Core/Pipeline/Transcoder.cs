using System.Globalization;
using Pu.Core.Common;
using Pu.Core.Planning;

namespace Pu.Core.Pipeline;

public sealed record TranscodeProgress(double Fraction, TimeSpan Position, TimeSpan Total);

/// <summary>
/// ffmpeg 执行：-progress pipe:1 解析 out_time_us 算进度。
/// 硬件解码失败时自动去掉 -hwaccel 软解重试一次；失败删除残片。
/// </summary>
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
        var result = await RunFfmpegAsync(input, plan.EffectiveInputArgs, plan.OutputArgs,
            outputPath, plan, totalDurationUs, progress, ct);
        if (result.ExitCode != 0 && plan.EffectiveInputArgs.Length > 0)
        {
            // 硬件解码失败（驱动/格式不支持）→ 纯软件解码重试一次
            result = await RunFfmpegAsync(input, [], plan.OutputArgs,
                outputPath, plan, totalDurationUs, progress, ct);
        }

        if (result.ExitCode != 0)
        {
            TryDelete(outputPath);
            if (plan.Hls) TryDelete(Path.GetDirectoryName(outputPath)!); // 临时目录整删
            throw new InvalidOperationException($"ffmpeg 失败: {Trim(result.StdErr)}");
        }

        progress?.Report(new TranscodeProgress(
            1, TimeSpan.FromMicroseconds(totalDurationUs), TimeSpan.FromMicroseconds(totalDurationUs)));
    }

    private static async Task<ProcessResult> RunFfmpegAsync(
        string input, string[] inputArgs, string[] outputArgs, string outputPath,
        TranscodePlan plan, long totalDurationUs,
        IProgress<TranscodeProgress>? progress, CancellationToken ct)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostats" };
        args.AddRange(inputArgs);
        args.Add("-i");
        args.Add(input);
        args.AddRange(outputArgs);
        if (plan.Hls)
        {
            // 分片与 m3u8 同目录；路径用正斜杠避免转义问题
            var seg = Path.Combine(Path.GetDirectoryName(outputPath)!, "seg_%05d.ts").Replace('\\', '/');
            args.AddRange(["-hls_segment_filename", seg]);
        }
        // 显式指定封装格式：产物可能先写 .tmp 临时名，扩展名推断会失败
        args.AddRange(["-f", MuxerNameFor(plan)]);
        args.AddRange(["-progress", "pipe:1", outputPath]);
        if (Environment.GetEnvironmentVariable("PU_DEBUG_ARGS") == "1")
            System.IO.File.AppendAllText(Path.Combine(Path.GetTempPath(), "pu-ffmpeg-args.log"),
                $"{DateTime.Now:HH:mm:ss} cwd={Environment.CurrentDirectory} exit?\nffmpeg {string.Join(' ', args)}\n\n");

        double lastReported = -1;
        return await ProcessRunner.RunAsync(FfmpegLocator.Exe, args, onStdoutLine: line =>
        {
            // 新版 ffmpeg 输出 out_time_us（微秒）；旧版只有 out_time_ms（历史遗留，单位实为微秒）；
            // 再兜一层 out_time（HH:MM:SS.ffffff）。三者任一解析成功即上报进度。
            long us;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                if (!long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out us)) return;
            }
            else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                if (!long.TryParse(line.AsSpan("out_time_ms=".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out us)) return;
            }
            else if (line.StartsWith("out_time=", StringComparison.Ordinal))
            {
                if (!TimeSpan.TryParse(line.AsSpan("out_time=".Length), CultureInfo.InvariantCulture, out var ts)) return;
                us = Math.Max(0, (long)ts.TotalMicroseconds);
            }
            else return;

            if (totalDurationUs <= 0) return;
            var fraction = Math.Clamp((double)us / totalDurationUs, 0.0, 1.0);
            if (fraction < 1 && fraction - lastReported < 0.01) return; // 限频：1% 粒度
            lastReported = fraction;
            progress?.Report(new TranscodeProgress(
                fraction,
                TimeSpan.FromMicroseconds(us),
                TimeSpan.FromMicroseconds(totalDurationUs)));
        }, cancellationToken: ct);
    }

    /// <summary>产物类型 → ffmpeg 封装器名（HLS 显式 hls；m4a 也走 mp4 封装器）。</summary>
    private static string MuxerNameFor(TranscodePlan plan) => plan.Hls ? "hls" : plan.OutputExtension switch
    {
        "mp4" or "m4a" => "mp4",
        var ext => ext,
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 尽力而为 */ }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* 尽力而为 */ }
    }

    private static string Trim(string s)
    {
        var t = s.Trim();
        return t.Length > 500 ? t[^500..] : t;
    }
}
