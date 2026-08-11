using System.Text;
using Pu.Core.Cache;
using Pu.Core.Pipeline;
using Pu.Core.Planning;
using Pu.Core.Probe;
using Pu.Core.Serving;

namespace Pu.App;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向环境忽略 */ }

        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?" or "help")
        {
            PrintUsage();
            return 0;
        }
        if (args[0] == "--version")
        {
            Console.WriteLine("pu~ 0.1.0 (M1)");
            return 0;
        }
        if (Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("文件夹模式（列表页）在 M2 提供，请先传入视频文件。");
            return 2;
        }

        var input = Path.GetFullPath(args[0]);
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"找不到文件: {input}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ui = new object();

        try
        {
            Console.WriteLine($"pu~ 分析 {input}");
            var info = await MediaProbe.ProbeAsync(input, cts.Token);
            Console.WriteLine($"  {Describe(info)}");

            var encoders = await EncoderCatalog.DetectAsync(cts.Token);
            var plan = TranscodePlan.Create(info, encoders, input);
            Console.WriteLine($"  计划: {plan.Explanation}");

            if (plan.Kind == PlanKind.Unsupported)
            {
                Console.Error.WriteLine($"无法处理: {plan.Explanation}");
                return 3;
            }

            string artifact;
            if (plan.Kind == PlanKind.ServeOriginal)
            {
                artifact = input;
            }
            else
            {
                var dir = CacheKey.ArtifactDirFor(input);
                artifact = Path.Combine(dir, $"out.{plan.OutputExtension}");
                if (File.Exists(artifact))
                {
                    Console.WriteLine("  缓存命中，直接出链");
                }
                else
                {
                    Directory.CreateDirectory(dir);
                    var progress = new Progress<TranscodeProgress>(p =>
                    {
                        lock (ui)
                        {
                            if (p.Fraction >= 1) Console.Write("\r  ✓ 转码完成    ");
                            else Console.Write($"\r  转码中 {p.Fraction * 100:F0}%    ");
                        }
                    });
                    await Transcoder.TranscodeAsync(input, plan, artifact, info.DurationUs, progress, cts.Token);
                    Console.WriteLine();
                }
            }

            var server = await SessionServer.StartAsync(ct: cts.Token);
            var contentType = plan.Kind == PlanKind.ServeOriginal ? ContentTypes.ForMedia(artifact) : "video/mp4";
            var session = server.Register(artifact, contentType, Path.GetFileNameWithoutExtension(input));

            Console.WriteLine();
            Console.WriteLine($"✓ 就绪：{session.Title}");
            Console.WriteLine($"  手机/平板（同 Wi-Fi）: http://{server.LanIp ?? "<未找到局域网 IP>"}:{server.Port}/s/{session.Token}");
            Console.WriteLine($"  本机测试            : http://localhost:{server.Port}/s/{session.Token}");
            Console.WriteLine();
            Console.WriteLine("按 Ctrl+C 停止服务");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
            catch (OperationCanceledException) { /* Ctrl+C */ }
            await server.StopAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    private static string Describe(MediaInfo info) =>
        info.Video is { } v
            ? $"{v.Codec} {v.BitDepth}bit {v.Width}×{v.Height} / 音频 {info.Audio?.Codec ?? "无"} / {info.FormatName} / {FormatSize(info.SizeBytes)}"
            : $"{info.FormatName} / {FormatSize(info.SizeBytes)}";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        _ => $"{bytes} B",
    };

    private static void PrintUsage()
    {
        Console.WriteLine("""
            pu~ —— 右键视频，扫码即播（M1：命令行版）

            用法:
              pu <视频文件>    分析、按需转码、启动服务并输出播放链接
              pu --help       显示本帮助
              pu --version    显示版本

            按 Ctrl+C 停止服务。
            """);
    }
}
