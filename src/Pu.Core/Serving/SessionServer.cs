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
    // 文件夹文件“正在打开”的在途任务：并发双击同一文件只转一次。
    // 用 Lazy 包任务工厂：GetOrAdd 的工厂本身可能并发执行多次，但只有胜出的
    // Lazy 会被访问 Value——落选 Lazy 的工厂永远不会运行，不会启动多余探测/转码
    private readonly ConcurrentDictionary<string, Lazy<Task<MediaJob>>> _opening = new(StringComparer.Ordinal);
    // 单文件模式同源去重：源文件指纹（路径|大小|mtime）→ job token。
    // 指纹或策略变体任一变化（源被替换 / 运行期改 transcode 配置）→ 不复用，重新生产
    private readonly ConcurrentDictionary<string, string> _bySource = new(StringComparer.OrdinalIgnoreCase);
    // 同源提交的在途去重：并发提交同一文件只跑一次探测/注册（Lazy 保证工厂只执行一次）
    private readonly ConcurrentDictionary<string, Lazy<Task<MediaJob>>> _submitting = new(StringComparer.OrdinalIgnoreCase);
    // 后台任务登记（转码/抽字幕）：StopAsync 取消后逐个等待退出，防止关闭时 ffmpeg 仍在写缓存
    private readonly ConcurrentDictionary<string, Task> _background = new(StringComparer.Ordinal);
    // 最近一次媒体传输时刻（token → ticks）：LRU 保护判定用。
    // HLS 播放是每 ~2s 一个分片请求，用“窗口内有过传输”而非“正在传输”判定，
    // 避免分片间隙被淘汰；播放停止超过窗口期后条目重新交给 LRU。
    private readonly ConcurrentDictionary<string, long> _lastTransfer = new(StringComparer.Ordinal);
    internal static TimeSpan TransferProtectWindow { get; set; } = TimeSpan.FromMinutes(2);
    // 服务生命周期令牌：独立于 HTTP 请求（客户端断开不连坐取消），仅随服务停止统一取消
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WebApplication _app;

    /// <summary>服务生命周期令牌：传给后台任务（探测/转码/抽字幕），StopAsync 时统一取消。</summary>
    public CancellationToken ShutdownToken => _lifetime.Token;

    /// <summary>媒体传输开始：刷新该条目的最近传输时刻（LRU 保护窗口内不淘汰）。</summary>
    internal void BeginTransfer(string token) => _lastTransfer[token] = DateTime.UtcNow.Ticks;

    /// <summary>请求级诊断日志（Pu.App 注入；不看 UA/Range 排查不了移动端播放问题）。</summary>
    public static Action<string>? LogSink;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private int _activeJobs;
    private long _lastMediaLogTicks;

    public int Port { get; }
    public string? LanIp { get; }
    public string? LatestUrl { get; private set; }
    public int JobCount => _jobs.Count;
    /// <summary>会话数（信息用；空闲退出只看 ActiveJobCount + IdleFor，见 Program.IdleWatchAsync）。</summary>
    public int SessionCount => _jobs.Count + _folders.Count;

    /// <summary>正在转码的任务数（空闲退出只看这个 + IdleFor，不看 SessionCount）。</summary>
    public int ActiveJobCount => Volatile.Read(ref _activeJobs);
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

        app.MapGet("/s/{token}/media", (string token, HttpContext context) =>
        {
            server.Touch();
            if (!server._jobs.TryGetValue(token, out var job)) return Results.NotFound();
            if (job.State != JobState.Serving) return Results.Conflict();
            // 分片每 2s 拉一个，逐条打日志会疯狂写盘（2 小时电影 ≈ 3600 次）→ 10s 限频
            if (Environment.TickCount64 - Interlocked.Read(ref server._lastMediaLogTicks) >= 10_000)
            {
                Interlocked.Exchange(ref server._lastMediaLogTicks, Environment.TickCount64);
                LogSink?.Invoke($"media 请求 {context.Connection.RemoteIpAddress}"
                    + $" range=[{context.Request.Headers.Range}]"
                    + $" ua={context.Request.Headers.UserAgent}");
            }
            // HLS：给 m3u8（播放器会按相对路径拉分片）；MP4：Range 直服
            server.BeginTransfer(token);
            return job.IsHls
                ? Results.File(job.ArtifactPath, "application/vnd.apple.mpegurl")
                : Results.File(job.ArtifactPath, job.ContentType, enableRangeProcessing: true);
        });

        // HLS 分片 / m3u8 内引用的任何文件（防目录穿越：只放行普通文件名）
        app.MapGet("/s/{token}/hls/{file}", (string token, string file, HttpContext context) =>
        {
            server.Touch();
            if (!server._jobs.TryGetValue(token, out var job)) return Results.NotFound();
            if (job.State != JobState.Serving || !job.IsHls) return Results.NotFound();
            if (file.Length == 0 || file.Length > 64
                || !file.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return Results.BadRequest();
            // 只提供播放列表与 TS 分片：产物目录内的其它文件（如复用清单 index.m3u8.json）一律不外泄
            if (!file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();
            var path = Path.Combine(Path.GetDirectoryName(job.ArtifactPath)!, file);
            if (!File.Exists(path)) return Results.NotFound();
            server.BeginTransfer(token);
            var type = file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? "application/vnd.apple.mpegurl"
                : "video/mp2t";
            return Results.File(path, type, enableRangeProcessing: true);
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
            // 只允许给本服务自己的 URL 出码：防止拿 token 生成指向钓鱼站的二维码
            if (!IsOwnUrl(u, server.Port, server.LanIp)) return Results.BadRequest();
            return Results.Bytes(QrPng(u!), "image/png");
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

        app.MapGet("/assets/hls.min.js", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Text(EmbeddedWeb.HlsJs, "application/javascript");
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
            // 不用请求级 CancellationToken：客户端断开（拿到 URL 后关闭连接）不能连坐取消后台转码；
            // 用服务生命周期令牌：只有服务停止时才取消（StopAsync 内部统一处理）
            var job = await server.OpenFolderFileAsync(folder, index, server.ShutdownToken);
            return Results.Json(new OpenResultDto(server.UrlFor(job)), JobStatusJsonContext.Default.OpenResultDto);
        });

        await app.StartAsync(ct);
        return server;
    }

    /// <summary>提交一个媒体文件：探测 → 决策 → 注册 job，转码/抽字幕在后台并行跑。
    /// 同一源文件（指纹一致且策略变体一致）重复/并发提交直接复用已有 job，不重复转码。</summary>
    public async Task<MediaJob> SubmitAsync(string sourcePath, CancellationToken ct = default)
    {
        // 源文件指纹（路径|大小|mtime）作去重键：源被替换 → 指纹变 → 不误复用旧任务
        var key = CacheKey.For(sourcePath);
        if (await TryReuseExistingAsync(key) is { } live)
        {
            LatestUrl = UrlFor(live);
            return live;
        }

        // 在途去重：先查后建的窗口期里并发提交同一文件 → 共享同一个任务。
        // GetOrAdd 的工厂本身可并发执行多次，若直接在工厂里启动异步任务，落选任务
        // 仍会继续跑（多个 ffmpeg 写同一临时产物）。用 Lazy<Task> 包住工厂：
        // 只有胜出的 Lazy 会被访问 Value，落选者连工厂都不会运行。
        var lazy = _submitting.GetOrAdd(key, _ => new Lazy<Task<MediaJob>>(
            () => SubmitCoreAsync(sourcePath, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            _submitting.TryRemove(KeyValuePair.Create(key, lazy));
        }
    }

    /// <summary>同源复用核对：指纹命中后还要比对策略变体。
    /// 运行期修改 transcode 配置（auto↔always 等）→ 变体不同 → 不复用旧 job，立即重转。</summary>
    private async Task<MediaJob?> TryReuseExistingAsync(string key)
    {
        if (_bySource.TryGetValue(key, out var token)
            && _jobs.TryGetValue(token, out var live) && live.State != JobState.Failed)
        {
            var policy = PuConfig.TranscodePolicy;
            // 只有 ForceGpu + 有视频的变体才依赖编码器目录（lazy，探测一次后缓存）；其余纯配置判定
            var want = policy == TranscodePolicy.ForceGpu && live.HasVideo
                ? $"gpu:{(await Catalog.Value).PreferredH264Encoder};fmt:{TranscodePlan.FormatVersion}"
                : $"fmt:{TranscodePlan.FormatVersion}";
            if (live.Variant == want)
                return live;
        }
        return null;
    }

    private async Task<MediaJob> SubmitCoreAsync(string sourcePath, CancellationToken ct)
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
        // 产物变体 = 转码策略 + 产物格式版本（旧参数产出的缓存自动失效重转）
        var variant = VariantFor(policy, info.Video is not null, catalog ?? EncoderCatalog.SoftwareOnly);

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
                CacheManager.TouchEntry(hit); // 中央缓存命中：刷新 LRU 标记（HLS 要 touch 条目目录本身）
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
            Token = RandomNumberGenerator.GetHexString(32),
            SourcePath = sourcePath,
            Title = Path.GetFileNameWithoutExtension(sourcePath),
            SourceDescription = Describe(info),
            ArtifactPath = artifact,
            ContentType = contentType,
            PlanExplanation = plan.Explanation,
            IsHls = !passthrough && plan.Hls,
            Variant = variant,
            HasVideo = info.Video is not null,
        };
        _jobs[job.Token] = job;
        _bySource[CacheKey.For(sourcePath)] = job.Token;
        LatestUrl = UrlFor(job);

        if (target is null)
        {
            // 零处理 / 复用命中：字幕后台抽取（大文件多条字幕要完整读一遍源文件，
            // 不阻塞提交——窗口立刻有内容，抽完自动置可播）。
            // 字幕按源文件身份隔离，防止同目录多集互相覆盖：
            //   直出           → 中央缓存 {key}\subs（不往源目录写垃圾）
            //   中央缓存命中   → {缓存key}\subs（key 含源指纹，天然隔离）
            //   就地产物命中   → .pu\{指纹}\subs（指纹 = 路径|大小|mtime；.pu 目录从产物路径反推，
            //                    不依赖 artifactDir —— HLS 的 artifactDir 是产物目录本身）
            var subsRoot = passthrough
                ? CacheKey.ArtifactDirFor(sourcePath)
                : ArtifactLocator.SidecarDirOf(artifact) is { } sd
                    ? Path.Combine(sd, CacheKey.For(sourcePath))
                    : CacheManager.EntryDirFor(artifact) is { } entry
                        ? entry // 中央缓存命中：{缓存key}，与生产路径（target.WorkDir）一致
                        : artifactDir;
            _ = Track(job.Token, ExtractSubsAndServeAsync(job, sourcePath, info, subsRoot, _lifetime.Token));
        }
        else
        {
            Interlocked.Increment(ref _activeJobs);
            _ = Track(job.Token, RunJobAsync(job, sourcePath, info, plan, target, variant, _lifetime.Token));
        }
        var protectedDirs = ProtectedEntryDirs();
        CacheManager.MaybeEvict(skipEntry: protectedDirs.Contains);
        return job;
    }

    /// <summary>产物变体 = 转码策略|编码器|格式版本（旧参数/旧策略产出的缓存自动失效重转）。</summary>
    private static string VariantFor(TranscodePolicy policy, bool hasVideo, EncoderCatalog catalog)
        => policy == TranscodePolicy.ForceGpu && hasVideo
            ? $"gpu:{catalog.PreferredH264Encoder};fmt:{TranscodePlan.FormatVersion}"
            : $"fmt:{TranscodePlan.FormatVersion}";

    /// <summary>登记后台任务，供 StopAsync 取消后等待退出；完成即自动移除。
    /// 移除登记统一由 Track 管理：任务在登记前就已启动，无字幕路径可以同步完成并
    /// 先执行自身的 TryRemove——若由任务在 finally 里移除，会早于登记写入，
    /// 已完成任务便永久留在登记表。续延挂在登记之后，不存在“先移除后登记”的窗口。</summary>
    private Task Track(string key, Task task)
    {
        _background[key] = task;
        // 已同步完成的任务：ExecuteSynchronously 让续延立即执行，登记后马上清掉
        _ = task.ContinueWith(t => _background.TryRemove(key, out _),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>需要 LRU 保护的中央缓存条目：转码中（.tmp 写入中）与最近窗口内有传输的条目。
    /// 已就绪且长时间无播放的条目放行给 LRU——否则文件夹会话打开过的每一集都永久占住 20GB。</summary>
    internal HashSet<string> ProtectedEntryDirs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.Ticks;
        foreach (var job in _jobs.Values)
        {
            if (job.State == JobState.Failed) continue;
            var recentlyPlayed = _lastTransfer.TryGetValue(job.Token, out var last)
                && now - last <= TransferProtectWindow.Ticks;
            if (job.State != JobState.Transcoding && !recentlyPlayed) continue;
            if (CacheManager.EntryDirFor(job.ArtifactPath) is { } entry)
                set.Add(entry);
        }
        return set;
    }

    /// <summary>按 token 查 job 状态（WPF 文件夹行徽标用）；查不到返回 null。</summary>
    public JobState? JobStateFor(string token)
        => _jobs.TryGetValue(token, out var job) ? job.State : null;

    /// <summary>直出/复用命中：后台抽字幕，完成后置可播（抽字幕失败只丢字幕，不拖垮视频）。</summary>
    private async Task ExtractSubsAndServeAsync(
        MediaJob job, string sourcePath, MediaInfo info, string subsRoot, CancellationToken ct)
    {
        try
        {
            var subs = await ExtractSubsSafeAsync(sourcePath, info, subsRoot, ct);
            job.SetServing(subs);
        }
        catch (OperationCanceledException)
        {
            job.SetFailed("已取消");
        }
        catch (Exception ex)
        {
            job.SetFailed(ex.Message);
        }
        // 从登记表移除统一由 Track 的完成续延负责（见 Track）
    }

    /// <summary>文件夹模式：扫描媒体文件并注册列表会话（文件按需懒加载，不预转码）。</summary>
    public async Task<FolderJob> SubmitFolderAsync(string folderPath, CancellationToken ct = default)
    {
        var files = await Task.Run(() => FolderScan.Scan(folderPath, MediaExtensions.Defaults), ct);
        if (files.Count == 0)
            throw new InvalidOperationException("文件夹里没有媒体文件");
        var folder = new FolderJob
        {
            Token = RandomNumberGenerator.GetHexString(32),
            FolderPath = folderPath,
            Title = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar)),
            Files = files,
        };
        _folders[folder.Token] = folder;
        LatestUrl = UrlForFolder(folder);
        return folder;
    }

    /// <summary>点开文件夹里的一个文件 → 创建（或复用）媒体任务，返回 job（URL 用 UrlFor 拼）。
    /// 指纹/策略变体的复用核对统一交给 SubmitAsync，这里只做并发在途去重。</summary>
    public async Task<MediaJob> OpenFolderFileAsync(FolderJob folder, int index, CancellationToken ct = default)
    {
        // 在途去重：先查后建的窗口期里并发双击同一文件 → 共享同一个任务，只转一次。
        // 同 SubmitAsync：Lazy 包工厂，落选的并发提交不会启动多余探测
        var key = $"{folder.Token}:{index}";
        var lazy = _opening.GetOrAdd(key, _ => new Lazy<Task<MediaJob>>(
            () => OpenFolderFileCoreAsync(folder, index, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            _opening.TryRemove(KeyValuePair.Create(key, lazy));
        }
    }

    private async Task<MediaJob> OpenFolderFileCoreAsync(FolderJob folder, int index, CancellationToken ct)
    {
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
            if (plan.Hls)
            {
                Directory.CreateDirectory(target.TempPath); // ffmpeg 需要分片目录存在
                if (Environment.GetEnvironmentVariable("PU_DEBUG_ARGS") == "1")
                    System.IO.File.AppendAllText(Path.Combine(Path.GetTempPath(), "pu-ffmpeg-args.log"),
                        $"{DateTime.Now:HH:mm:ss} tmp={target.TempPath} exists={Directory.Exists(target.TempPath)}\n");
            }
            var progress = new Progress<TranscodeProgress>(p => job.UpdateProgress(p.Fraction));
            // HLS 临时目录 = {产物}.tmp；ffmpeg 写 temp/index.m3u8 + 分片，成功整目录改名
            var outputPath = plan.Hls
                ? Path.Combine(target.TempPath, "index.m3u8")
                : target.TempPath;
            var transcode = Transcoder.TranscodeAsync(sourcePath, plan, outputPath, info.DurationUs, progress, ct);
            // 字幕抽取失败不连坐视频：视频照常可播，只是没有字幕。
            // 字幕目录按源文件指纹隔离（.pu/{指纹} 或中央 {key}），同目录多集互不覆盖
            var subs = ExtractSubsSafeAsync(sourcePath, info, SubsRootFor(target, sourcePath), ct);
            await Task.WhenAll(transcode, subs);
            if (plan.Hls)
            {
                var finalDir = Path.GetDirectoryName(target.ArtifactPath)!;
                if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true); // 覆盖旧产物
                Directory.Move(target.TempPath, finalDir);
            }
            else if (!string.Equals(target.TempPath, target.ArtifactPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(target.TempPath, target.ArtifactPath, overwrite: true);
            }
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
            // 服务关闭取消：清掉半截临时产物，不让 ffmpeg 残留继续占缓存
            TryCleanTemp(plan, target);
            job.SetFailed("已取消");
        }
        catch (Exception ex)
        {
            // 转码成功但落位失败（如旧产物被播放器占用）等：清掉临时产物，防残留
            TryCleanTemp(plan, target);
            job.SetFailed(ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _activeJobs);
            // 从登记表移除统一由 Track 的完成续延负责（见 Track）
        }
    }

    /// <summary>字幕工作目录：按源文件指纹隔离（.pu/{指纹} 或中央 {key}/subs），
    /// 同一目录多集视频不共用 subs/，避免打开下一集覆盖上一集仍在播的字幕。</summary>
    private static string SubsRootFor(ArtifactTarget target, string sourcePath)
        => target.Sidecar
            ? Path.Combine(target.WorkDir, CacheKey.For(sourcePath))
            : target.WorkDir;

    /// <summary>尽力清理临时产物：HLS 删临时整目录，其余删 .tmp 文件。</summary>
    private static void TryCleanTemp(TranscodePlan plan, ArtifactTarget target)
    {
        try
        {
            if (plan.Hls)
            {
                if (Directory.Exists(target.TempPath))
                    Directory.Delete(target.TempPath, recursive: true);
            }
            else if (File.Exists(target.TempPath))
            {
                File.Delete(target.TempPath);
            }
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>抽字幕；失败只丢字幕（返回空表），不抛异常拖垮视频本身。</summary>
    private static async Task<List<SubtitleFile>> ExtractSubsSafeAsync(
        string sourcePath, MediaInfo info, string subsRoot, CancellationToken ct)
    {
        try
        {
            return await SubtitleExtractor.ExtractAsync(sourcePath, info, subsRoot, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSink?.Invoke($"字幕抽取失败（忽略，继续播放）: {ex.Message}");
            return [];
        }
    }

    private JobStatusDto ToDto(MediaJob job)
    {
        var subs = job.Subtitles.Select(s => new SubtitleDto(
            s.StreamIndex, s.Codec, s.Language, s.Title,
            string.IsNullOrEmpty(s.Title) && LangNames.TryGetValue(s.Language, out var n) ? n : s.Title)).ToList();
        return new JobStatusDto(
            job.State.ToString().ToLowerInvariant(), job.Progress, job.Error,
            job.Title, job.PlanExplanation, subs, job.IsHls);
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

    private static bool IsOwnUrl(string? u, int port, string? lanIp)
    {
        if (string.IsNullOrEmpty(u) || u.Length > 512) return false;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (uri.Port != port) return false;
        return uri.Host is "localhost" or "127.0.0.1" or "::1"
            || (lanIp is not null && string.Equals(uri.Host, lanIp, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>停止服务：先取消生命周期令牌（ffmpeg 转码/抽字幕随之终止），
    /// 等待全部后台任务退出后再停 Kestrel——关闭时不留继续写缓存的 ffmpeg 进程。
    /// 幂等：重复调用（含 DisposeAsync 的隐式调用）安全。</summary>
    public async Task StopAsync()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { } // 已 Dispose 过：取消无意义，继续等待
        // 循环取快照：取消时刻仍有提交在途时，新登记的后台任务可能落在第一轮快照之后；
        // 所有后台任务都以生命周期令牌为取消源，取消后必然快速退出，循环必然收敛。
        while (true)
        {
            var pending = _background.Values.ToArray();
            if (pending.Length == 0) break;
            try { await Task.WhenAll(pending); } catch { /* 任务内部均已捕获异常 */ }
        }
        await _app.StopAsync();
    }

    /// <summary>释放：与 StopAsync 同一套取消+等待逻辑（Kestrel 停止幂等），
    /// 未显式 StopAsync 的 await using 退出路径同样不会遗留运行中的 ffmpeg。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Dispose();
        await _app.DisposeAsync();
    }
}
