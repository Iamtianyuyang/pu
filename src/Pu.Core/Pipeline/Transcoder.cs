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
    /// <summary>
    /// 硬解/硬编失败的 stderr 特征串（不区分大小写）。命中才走兜底重试；
    /// 输出侧错误（磁盘满 "No space left on device"、坏文件 "Invalid data found when processing input"、
    /// 容器/参数错误）不命中 → 立即报错——原逻辑对这类错误也会整轮重跑 2 次，
    /// 磁盘满时白白多编两遍全片才报错。
    /// 看门狗超时（ExitCode == -1，进程无进展被终止）也算硬件相关：卡死常由驱动问题引起，
    /// README 承诺的「卡死 → 硬解/硬编兜底」路径保持生效。
    /// </summary>
    private static readonly string[] HwFailureMarkers =
    [
        "cuda", "cuvid", "nvenc", "qsv", "mfx", "amf", "d3d11", "vaapi", "vdpau", "dxva",
        "hwaccel", "hardware", "driver", "cannot load", "initialization failed", "failed to initialize",
    ];

    private static bool IsHwFailure(ProcessResult result)
        => result.ExitCode == -1 // 看门狗超时：可能由驱动问题引起，保留兜底路径
        || HwFailureMarkers.Any(m => result.StdErr.Contains(m, StringComparison.OrdinalIgnoreCase));

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
        if (result.ExitCode != 0 && plan.EffectiveInputArgs.Length > 0 && IsHwFailure(result))
        {
            // 硬件解码失败（驱动/格式不支持）→ 清掉首轮残留分片/半截文件，纯软件解码重试一次
            CleanupArtifact(outputPath, plan);
            result = await RunFfmpegAsync(input, [], plan.OutputArgs,
                outputPath, plan, totalDurationUs, progress, ct);
        }
        if (result.ExitCode != 0 && plan.EncoderName is { } enc && enc != "libx264" && IsHwFailure(result))
        {
            // 硬件编码器本身失败（驱动崩溃/分辨率超限）→ 清残片，libx264 软编兜底一次
            CleanupArtifact(outputPath, plan);
            var fallback = plan.WithSoftwareEncoder();
            result = await RunFfmpegAsync(input, fallback.EffectiveInputArgs, fallback.OutputArgs,
                outputPath, fallback, totalDurationUs, progress, ct);
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

    /// <summary>清掉本轮失败产出的分片/半截文件：HLS 清临时目录内全部文件，其余删目标文件。</summary>
    private static void CleanupArtifact(string outputPath, TranscodePlan plan)
    {
        if (plan.Hls)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (dir is not null)
            {
                try { foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f); } catch { /* 尽力而为 */ }
            }
        }
        else TryDelete(outputPath);
    }

    /// <summary>看门狗检查间隔；-progress 正常每 ~0.5s 输出一行，间隔取 30s 足够灵敏。</summary>
    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(30);
    /// <summary>无进展阈值：120 秒没有任何 stdout 输出 → 判定 ffmpeg 卡死（驱动/坏文件），终止并走兜底。</summary>
    private const long StallTimeoutMs = 120_000;

    private static async Task<ProcessResult> RunFfmpegAsync(
        string input, string[] inputArgs, string[] outputArgs, string outputPath,
        TranscodePlan plan, long totalDurationUs,
        IProgress<TranscodeProgress>? progress, CancellationToken ct)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostats", "-nostdin" };
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

        // 卡死看门狗：任何一行 stdout 输出都会刷新计时；超时未输出 → 取消本次运行。
        // 与调用方取消区分：调用方（服务关闭）的取消原样上抛；卡死按“失败”返回，
        // 交给 TranscodeAsync 现有的硬解/硬编兜底逻辑接手（可能换软解/软编后能继续）。
        // 看门狗挂在 stallCts 上：RunAsync 正常结束/取消后，finally 的 Cancel 立即终止它，
        // 不会空转到超时阈值才退出。
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastLineTicks = Environment.TickCount64;
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(StallCheckInterval, stallCts.Token);
                    if (Environment.TickCount64 - Interlocked.Read(ref lastLineTicks) >= StallTimeoutMs)
                    {
                        try { stallCts.Cancel(); } catch (ObjectDisposedException) { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* 令牌取消（正常结束 / 服务关闭）：看门狗退出 */ }
        }, CancellationToken.None);

        double lastReported = -1;
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(FfmpegLocator.Exe, args, onStdoutLine: line =>
            {
                Interlocked.Exchange(ref lastLineTicks, Environment.TickCount64);
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
            }, cancellationToken: stallCts.Token, maxStdoutLines: 0, maxStderrLines: 200);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 看门狗取消（卡死）：按失败返回，走 TranscodeAsync 的重试/软编兜底
            return new ProcessResult(-1, "", "ffmpeg 长时间无进展（可能卡死），已终止");
        }
        finally
        {
            try { stallCts.Cancel(); } catch (ObjectDisposedException) { }
            await watchdog; // 看门狗随令牌立即退出；内部已捕获取消异常，不会抛
        }
        return result;
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
