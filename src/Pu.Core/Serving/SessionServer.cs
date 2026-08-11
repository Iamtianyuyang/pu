using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Pu.Core.Cache;
using Pu.Core.Pipeline;
using Pu.Core.Planning;
using Pu.Core.Probe;
using QRCoder;

namespace Pu.Core.Serving;

/// <summary>
/// Kestrel 会话服务（方案.md 第六、九节）：
/// - 监听 0.0.0.0（http.sys 需要管理员，Kestrel 不需要 —— 硬约束）
/// - 二维码/状态页在转码开始的瞬间就给出，转完自动起播
/// - token 鉴权：不知道 URL 的人拿不到内容
/// - Range 交给 Results.File，Safari 拖动进度条可用
/// 路由：
///   /s/{token}            播放/状态页
///   /s/{token}/status     轮询 JSON（转码进度 / 字幕列表）
///   /s/{token}/media      媒体本体（Range）
///   /s/{token}/sub/{i}    WebVTT 字幕
///   /s/{token}/qr.png     URL 二维码
/// </summary>
public sealed class SessionServer : IAsyncDisposable
{
    private static readonly Lazy<Task<EncoderCatalog>> Catalog = new(static () => EncoderCatalog.DetectAsync());
    private static readonly Dictionary<string, string> LangNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chi"] = "简体中文", ["zho"] = "简体中文", ["eng"] = "English", ["jpn"] = "日本語",
        ["kor"] = "한국어", ["fra"] = "Français", ["deu"] = "Deutsch", ["rus"] = "Русский",
        ["spa"] = "Español", ["por"] = "Português", ["ita"] = "Italiano", ["tha"] = "ไทย",
        ["vie"] = "Tiếng Việt", ["ara"] = "العربية", ["hin"] = "हिन्दी", ["may"] = "Melayu",
        ["ind"] = "Bahasa Indonesia",
    };

    private readonly ConcurrentDictionary<string, MediaJob> _jobs = new(StringComparer.Ordinal);
    private readonly WebApplication _app;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public int Port { get; }
    public string? LanIp { get; }
    public string? LatestUrl { get; private set; }
    public int JobCount => _jobs.Count;
    public TimeSpan IdleFor => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks));
    public static TimeSpan IdleTimeout => TimeSpan.FromMinutes(30);

    private SessionServer(WebApplication app, int port, string? lanIp)
    {
        _app = app;
        Port = port;
        LanIp = lanIp;
    }

    public static async Task<SessionServer> StartAsync(int preferredPort = 8000, CancellationToken ct = default)
    {
        var port = FindFreePort(preferredPort);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Any, port));
        var app = builder.Build();

        var server = new SessionServer(app, port, LanAddress.GetLanIpv4());

        app.MapGet("/s/{token}", (string token) =>
        {
            server.Touch();
            return server._jobs.TryGetValue(token, out _)
                ? Results.Content(EmbeddedWeb.IndexHtml, "text/html; charset=utf-8")
                : Results.NotFound();
        });

        app.MapGet("/s/{token}/media", (string token) =>
        {
            server.Touch();
            if (!server._jobs.TryGetValue(token, out var job)) return Results.NotFound();
            if (job.State != JobState.Serving) return Results.Conflict();
            return Results.File(job.ArtifactPath, job.ContentType, enableRangeProcessing: true);
        });

        app.MapGet("/s/{token}/status", (string token) =>
        {
            server.Touch();
            if (!server._jobs.TryGetValue(token, out var job)) return Results.NotFound();
            return Results.Json(server.ToDto(job), JobStatusJsonContext.Default.JobStatusDto);
        });

        app.MapGet("/s/{token}/qr.png", (string token, string? u) =>
        {
            server.Touch();
            if (!server._jobs.TryGetValue(token, out _)) return Results.NotFound();
            if (string.IsNullOrEmpty(u) || u.Length > 512
                || (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest();
            return Results.Bytes(QrPng(u), "image/png");
        });

        app.MapGet("/s/{token}/sub/{index:int}", (string token, int index) =>
        {
            server.Touch();
            if (!server._jobs.TryGetValue(token, out var job)) return Results.NotFound();
            var sub = job.Subtitles.FirstOrDefault(s => s.StreamIndex == index);
            return sub is null || !File.Exists(sub.VttPath)
                ? Results.NotFound()
                : Results.File(sub.VttPath, "text/vtt; charset=utf-8");
        });

        app.MapGet("/", () => Results.Text("pu~ is running"));

        await app.StartAsync(ct);
        return server;
    }

    /// <summary>提交一个媒体文件：探测 → 决策 → 注册 job，转码/抽字幕在后台并行跑。</summary>
    public async Task<MediaJob> SubmitAsync(string sourcePath, CancellationToken ct = default)
    {
        var info = await MediaProbe.ProbeAsync(sourcePath, ct);
        var plan = TranscodePlan.Create(info, await Catalog.Value, sourcePath);
        if (plan.Kind == PlanKind.Unsupported)
            throw new InvalidOperationException(plan.Explanation);

        var passthrough = plan.Kind == PlanKind.ServeOriginal;
        var artifactDir = passthrough ? Path.GetDirectoryName(sourcePath)! : CacheKey.ArtifactDirFor(sourcePath);
        var artifact = passthrough ? sourcePath : Path.Combine(artifactDir, $"out.{plan.OutputExtension}");
        var contentType = passthrough
            ? ContentTypes.ForMedia(sourcePath)
            : info.Video is null ? "audio/mp4" : "video/mp4";

        var job = new MediaJob
        {
            Token = RandomNumberGenerator.GetHexString(16),
            SourcePath = sourcePath,
            Title = Path.GetFileNameWithoutExtension(sourcePath),
            SourceDescription = Describe(info),
            ArtifactPath = artifact,
            ContentType = contentType,
            PlanExplanation = plan.Explanation,
        };
        _jobs[job.Token] = job;
        LatestUrl = UrlFor(job);

        if (passthrough || File.Exists(artifact))
        {
            // 零处理 / 缓存命中：抽完字幕直接可播
            var subs = await SubtitleExtractor.ExtractAsync(sourcePath, info, artifactDir, ct);
            job.SetServing(subs);
        }
        else
        {
            _ = RunJobAsync(job, sourcePath, info, plan, artifact, artifactDir, ct);
        }
        return job;
    }

    /// <summary>测试/外部用：注册一个已构造好的 job。</summary>
    public MediaJob Register(MediaJob job)
    {
        _jobs[job.Token] = job;
        LatestUrl = UrlFor(job);
        return job;
    }

    public string UrlFor(MediaJob job) => $"http://{LanIp ?? "localhost"}:{Port}/s/{job.Token}";

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private async Task RunJobAsync(
        MediaJob job, string sourcePath, MediaInfo info, TranscodePlan plan,
        string artifact, string artifactDir, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(artifactDir);
            var progress = new Progress<TranscodeProgress>(p => job.UpdateProgress(p.Fraction));
            var transcode = Transcoder.TranscodeAsync(sourcePath, plan, artifact, info.DurationUs, progress, ct);
            var subs = SubtitleExtractor.ExtractAsync(sourcePath, info, artifactDir, ct);
            await Task.WhenAll(transcode, subs);
            job.SetServing(await subs);
        }
        catch (OperationCanceledException)
        {
            job.SetFailed("已取消");
        }
        catch (Exception ex)
        {
            job.SetFailed(ex.Message);
        }
    }

    private JobStatusDto ToDto(MediaJob job)
    {
        var subs = job.Subtitles.Select(s => new SubtitleDto(
            s.StreamIndex, s.Codec, s.Language, s.Title,
            string.IsNullOrEmpty(s.Title) && LangNames.TryGetValue(s.Language, out var n) ? n : s.Title)).ToList();
        return new JobStatusDto(
            job.State.ToString().ToLowerInvariant(), job.Progress, job.Error,
            job.Title, job.PlanExplanation, job.SourcePath, subs);
    }

    private static string Describe(MediaInfo info)
    {
        var size = info.SizeBytes switch
        {
            >= 1L << 30 => $"{info.SizeBytes / (double)(1L << 30):F1} GB",
            >= 1L << 20 => $"{info.SizeBytes / (double)(1L << 20):F0} MB",
            _ => $"{info.SizeBytes} B",
        };
        return info.Video is { } v
            ? $"{v.Codec} {v.BitDepth}bit {v.Width}×{v.Height} / 音频 {info.Audio?.Codec ?? "无"} / {size}"
            : $"音频 {info.Audio?.Codec ?? "无"} / {size}";
    }

    private static byte[] QrPng(string url)
    {
        using var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(6);
    }

    private static int FindFreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 32; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException($"端口 {preferred}–{preferred + 31} 均被占用");
    }

    public Task StopAsync() => _app.StopAsync();
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
