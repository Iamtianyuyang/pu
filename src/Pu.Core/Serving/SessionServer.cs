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
using Pu.Core.Common;

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
    private readonly ConcurrentDictionary<string, FolderJob> _folders = new(StringComparer.Ordinal);
    private readonly WebApplication _app;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public int Port { get; }
    public string? LanIp { get; }
    public string? LatestUrl { get; private set; }
    public int JobCount => _jobs.Count;
    public int SessionCount => _jobs.Count + _folders.Count;
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

        app.MapGet("/assets/pu-logo.png", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Bytes(EmbeddedWeb.LogoPng, "image/png");
        });

        app.MapGet("/", () => Results.Text("pu~ is running"));

        // ── 文件夹模式：列表页 / 状态轮询 / 点开文件 ──
        app.MapGet("/f/{token}", (string token) =>
        {
            server.Touch();
            return server._folders.TryGetValue(token, out _)
                ? Results.Content(EmbeddedWeb.FolderHtml, "text/html; charset=utf-8")
                : Results.NotFound();
        });
        app.MapGet("/f/{token}/status", (string token) =>
        {
            server.Touch();
            if (!server._folders.TryGetValue(token, out var folder)) return Results.NotFound();
            return Results.Json(server.ToFolderDto(folder), JobStatusJsonContext.Default.FolderStatusDto);
        });
        app.MapPost("/f/{token}/open/{index:int}", async (string token, int index) =>
        {
            server.Touch();
            if (!server._folders.TryGetValue(token, out var folder)) return Results.NotFound();
            if (folder.Files.All(f => f.Index != index)) return Results.NotFound();
            // 不用请求级 CancellationToken：客户端断开（拿到 URL 后关闭连接）不能连坐取消后台转码
            var job = await server.OpenFolderFileAsync(folder, index, CancellationToken.None);
            return Results.Json(new OpenResultDto(server.UrlFor(job)), JobStatusJsonContext.Default.OpenResultDto);
        });

        await app.StartAsync(ct);
        return server;
    }

    /// <summary>提交一个媒体文件：探测 → 决策 → 注册 job，转码/抽字幕在后台并行跑。</summary>
    public async Task<MediaJob> SubmitAsync(string sourcePath, CancellationToken ct = default)
    {
        var info = await MediaProbe.ProbeAsync(sourcePath, ct);
        var policy = PuConfig.TranscodePolicy;
        // 只有会走进全转码分支的文件才需要编码器目录（探测要跑 ffmpeg -encoders + 硬件实测）；
        // 直出/Remux 直接跳过，窗口立刻有内容
        var needsEncoder = TranscodePlan.RequiresEncoder(info, policy);
        var catalog = needsEncoder ? await Catalog.Value : null;
        var plan = TranscodePlan.Create(info, catalog ?? EncoderCatalog.SoftwareOnly, sourcePath, policy);
        if (plan.Kind == PlanKind.Unsupported)
            throw new InvalidOperationException(plan.Explanation);

        var passthrough = plan.Kind == PlanKind.ServeOriginal;
        var variant = policy == TranscodePolicy.ForceGpu && info.Video is not null
            ? $"gpu:{catalog!.PreferredH264Encoder}" : null;

        // 产物落点：直出用源文件；命中复用直接播；否则就地（.pu\ 子目录）或中央缓存生产
        string artifact;
        string artifactDir;
        ArtifactTarget? target = null;
        if (passthrough)
        {
            artifact = sourcePath;
            artifactDir = Path.GetDirectoryName(sourcePath)!;
        }
        else if (ArtifactLocator.TryGetReusable(sourcePath, plan.OutputExtension, variant) is { } hit)
        {
            artifact = hit;
            artifactDir = Path.GetDirectoryName(hit)!;
            if (!ArtifactLocator.IsSidecarPath(hit))
                CacheManager.Touch(artifactDir); // 中央缓存命中：刷新 LRU 标记
        }
        else
        {
            target = ArtifactLocator.ForProduction(sourcePath, plan.OutputExtension, variant);
            artifact = target.ArtifactPath;
            artifactDir = target.WorkDir;
        }
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

        if (target is null)
        {
            // 零处理 / 复用命中：抽完字幕直接可播
            var subs = await SubtitleExtractor.ExtractAsync(sourcePath, info, artifactDir, ct);
            job.SetServing(subs);
        }
        else
        {
            _ = RunJobAsync(job, sourcePath, info, plan, target, variant, ct);
        }
        CacheManager.MaybeEvict();
        return job;
    }

    /// <summary>文件夹模式：扫描媒体文件并注册列表会话（文件按需懒加载，不预转码）。</summary>
    public async Task<FolderJob> SubmitFolderAsync(string folderPath, CancellationToken ct = default)
    {
        var files = await Task.Run(() => FolderScan.Scan(folderPath, MediaExtensions.Defaults), ct);
        if (files.Count == 0)
            throw new InvalidOperationException("文件夹里没有媒体文件");
        var folder = new FolderJob
        {
            Token = RandomNumberGenerator.GetHexString(16),
            FolderPath = folderPath,
            Title = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar)),
            Files = files,
        };
        _folders[folder.Token] = folder;
        LatestUrl = UrlForFolder(folder);
        return folder;
    }

    /// <summary>点开文件夹里的一个文件 → 创建（或复用）媒体任务，返回 job（URL 用 UrlFor 拼）。</summary>
    public async Task<MediaJob> OpenFolderFileAsync(FolderJob folder, int index, CancellationToken ct = default)
    {
        if (folder.OpenedToken(index) is { } existing && _jobs.TryGetValue(existing, out var existingJob)
            && existingJob.State != JobState.Failed)
            return existingJob; // 复用，避免重复转码

        var file = folder.Files.FirstOrDefault(f => f.Index == index)
            ?? throw new InvalidOperationException($"文件不存在: {index}");
        var job = await SubmitAsync(file.Path, ct);
        folder.MarkOpened(index, job.Token);
        return job;
    }

    public string UrlFor(MediaJob job) => $"http://{LanIp ?? "localhost"}:{Port}/s/{job.Token}";
    public string UrlForFolder(FolderJob folder) => $"http://{LanIp ?? "localhost"}:{Port}/f/{folder.Token}";

    /// <summary>测试/外部用：注册一个已构造好的 job。</summary>
    public MediaJob Register(MediaJob job)
    {
        _jobs[job.Token] = job;
        LatestUrl = UrlFor(job);
        return job;
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private async Task RunJobAsync(
        MediaJob job, string sourcePath, MediaInfo info, TranscodePlan plan,
        ArtifactTarget target, string? variant, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(target.WorkDir);
            var progress = new Progress<TranscodeProgress>(p => job.UpdateProgress(p.Fraction));
            // 先写临时文件，成功才改名成正式产物——就地模式下源目录不会看到半截 mp4
            var transcode = Transcoder.TranscodeAsync(sourcePath, plan, target.TempPath, info.DurationUs, progress, ct);
            var subs = SubtitleExtractor.ExtractAsync(sourcePath, info, target.WorkDir, ct);
            await Task.WhenAll(transcode, subs);
            if (!string.Equals(target.TempPath, target.ArtifactPath, StringComparison.OrdinalIgnoreCase))
                File.Move(target.TempPath, target.ArtifactPath, overwrite: true);
            if (target.Sidecar)
            {
                ArtifactLocator.WriteManifest(target.ArtifactPath, sourcePath, variant);
                ArtifactLocator.Register(target.ArtifactPath);
            }
            else
            {
                CacheManager.Touch(target.WorkDir); // 中央缓存新产物：更新 LRU 标记
            }
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

    private FolderStatusDto ToFolderDto(FolderJob folder)
    {
        var files = folder.Files.Select(f =>
        {
            var state = "new";
            if (folder.OpenedToken(f.Index) is { } token && _jobs.TryGetValue(token, out var job))
                state = job.State.ToString().ToLowerInvariant();
            return new FolderFileDto(f.Index, f.Name, f.SizeBytes, state);
        }).ToList();
        return new FolderStatusDto(folder.Title, files.Count, files);
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
