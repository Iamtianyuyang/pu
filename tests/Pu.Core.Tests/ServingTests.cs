using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Pu.Core.Cache;
using Pu.Core.Common;
using Pu.Core.Serving;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>
/// Kestrel 会话服务测试：
/// 状态页 / 状态 JSON / Range 媒体（Safari 拖进度条必需）/ 二维码 / 字幕 / token 404。
/// </summary>
public class ServingTests
{
    private static MediaJob ServingJob(string dir, string token, string? subtitleVtt = null)
    {
        var path = Path.Combine(dir, "sample.mp4");
        var bytes = new byte[1000];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        var job = new MediaJob
        {
            Token = token,
            SourcePath = path,
            Title = "sample",
            SourceDescription = "h264 8bit 128×72 / 音频 aac / 1 KB",
            ArtifactPath = path,
            ContentType = "video/mp4",
            PlanExplanation = "H.264 + AAC + MP4 已 faststart，原样直出",
        };
        var subs = subtitleVtt is null
            ? Array.Empty<SubtitleFile>()
            : new[] { new SubtitleFile(2, "subrip", "chi", "", subtitleVtt) };
        job.SetServing(subs);
        return job;
    }

    private static MediaJob TranscodingJob(string dir, string token)
    {
        var path = Path.Combine(dir, "sample.mp4");
        File.WriteAllBytes(path, new byte[64]);
        var job = new MediaJob
        {
            Token = token,
            SourcePath = path,
            Title = "sample",
            SourceDescription = "hevc 10bit 128×72 / 音频 aac / 1 KB",
            ArtifactPath = path,
            ContentType = "video/mp4",
            PlanExplanation = "HEVC 10bit 全转码 → H.264（libx264）",
        };
        return job; // 不 SetServing → 保持 Transcoding
    }

    [Fact]
    public async Task Range请求_返回206与正确字节段()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "a1");
        await using var server = await SessionServer.StartAsync(preferredPort: 18900);
        server.Register(job);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Range = new RangeHeaderValue(100, 199);
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/media");

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, body.Length);
        Assert.Equal(File.ReadAllBytes(job.ArtifactPath)[100..200], body);
        Assert.NotNull(resp.Content.Headers.ContentRange);
    }

    [Fact]
    public async Task 整文件请求_返回200与AcceptRanges()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "b1");
        await using var server = await SessionServer.StartAsync(preferredPort: 18901);
        server.Register(job);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/media");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("bytes", resp.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public async Task 品牌Logo_返回带Alpha的PNG与缓存头()
    {
        await using var server = await SessionServer.StartAsync(preferredPort: 18902);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/assets/pu-logo.png");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains("max-age=86400", resp.Headers.CacheControl?.ToString());
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], body[..4]);
        Assert.Equal(6, body[25]); // PNG IHDR color type 6 = RGBA
    }

    [Fact]
    public async Task 转码中_媒体返回409()
    {
        using var dir = new TempDir();
        var job = TranscodingJob(dir.Path, "c1");
        await using var server = await SessionServer.StartAsync(preferredPort: 18903);
        server.Register(job);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/media");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task 状态页_返回内嵌HTML()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "d1");
        await using var server = await SessionServer.StartAsync(preferredPort: 18904);
        server.Register(job);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("text/html", resp.Content.Headers.ContentType?.ToString());
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("噗~噗噗~~噗噗噗噗~~~~", html);
        Assert.Contains("<video", html);
    }

    [Fact]
    public async Task 状态JSON_字幕后补流程_SubsPending翻转()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "e2");
        await using var server = await SessionServer.StartAsync(preferredPort: 18913);
        job.SetServing(); // 视频先可播、字幕未定案（直出/复用命中的实际路径）
        server.Register(job);

        using var client = new HttpClient();
        async Task<bool> SubsPending()
        {
            var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/status");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("subsPending").GetBoolean();
        }

        Assert.True(await SubsPending()); // 未定案：页面继续慢轮询字幕
        job.SetSubtitles([new SubtitleFile(2, "subrip", "chi", "", "2.vtt")]);
        Assert.False(await SubsPending()); // 定案（空表也算）：页面停止轮询
    }

    [Fact]
    public async Task 超过Job上限_淘汰最老的已定案job()
    {
        using var dir = new TempDir();
        var sample = await MakeVideo(dir.Path, "cap.mp4",
            ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart"]);

        await using var server = await SessionServer.StartAsync(preferredPort: 18930);

        // 灌入 1100 个已定案假 job（Register 不探测不转码；CreatedTicks 构造时记录）
        for (var i = 0; i < 1100; i++)
        {
            var fake = new MediaJob
            {
                Token = RandomNumberGenerator.GetHexString(32),
                SourcePath = Path.Combine(dir.Path, $"fake{i}.mp4"),
                Title = $"fake{i}",
                SourceDescription = "fake",
                ArtifactPath = Path.Combine(dir.Path, $"fake{i}.mp4"),
                ContentType = "video/mp4",
                PlanExplanation = "",
            };
            fake.SetServing([]); // 已定案 + 字幕已定 → 可淘汰
            server.Register(fake);
        }
        Assert.Equal(1100, server.JobCount);

        // 真实提交触发上限淘汰：定案假 job 被清出，job 数收敛到上限 1024
        await server.SubmitAsync(sample);
        Assert.Equal(1024, server.JobCount);
    }

    [Theory]
    [InlineData("http://localhost:8000/s/abc", true, null)]
    [InlineData("http://127.0.0.1:8000/s/abc", true, null)]
    [InlineData("http://[::1]:8000/s/abc", true, null)]
    [InlineData("http://192.168.55.77:8000/s/abc", true, "192.168.55.77")] // LanIp
    [InlineData("https://localhost:8000/s/abc", false, null)]   // 服务只监听 http，https 连不上
    [InlineData("http://localhost:8000", true, null)]
    [InlineData("http://localhost:9000/s/abc", false, null)]       // 端口不符
    [InlineData("http://203.0.113.7:8000/s/abc", false, null)]     // TEST-NET-3 保留段，永不本机
    [InlineData("http://evil.example.com:8000/s/abc", false, null)]
    [InlineData("javascript:alert(1)", false, null)]               // 非 http(s)
    [InlineData("not a url", false, null)]
    public void 二维码URL白名单_只放行本机服务(string url, bool allowed, string? lanIp)
        => Assert.Equal(allowed, SessionServer.IsOwnUrl(url, 8000, lanIp));

    [Fact]
    public void 二维码URL白名单_本机主机名放行()
    {
        // 用户通过机器名打开状态页时二维码不能是空白
        Assert.True(SessionServer.IsOwnUrl($"http://{Environment.MachineName}:8000/s/abc", 8000, null));
        Assert.True(SessionServer.IsOwnUrl($"http://{Dns.GetHostName()}:8000/s/abc", 8000, null));
    }

    [Fact]
    public async Task 状态JSON_字段正确()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "e1");
        await using var server = await SessionServer.StartAsync(preferredPort: 18905);
        server.Register(job);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("serving", root.GetProperty("state").GetString());
        Assert.Equal(1.0, root.GetProperty("progress").GetDouble());
        Assert.Equal("sample", root.GetProperty("title").GetString());
        // 本地源路径不进状态 JSON（持 URL 的局域网访客不应看到宿主机文件路径）
        Assert.False(root.TryGetProperty("source", out _));
    }

    [Fact]
    public async Task 二维码_返回PNG()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "f1");
        await using var server = await SessionServer.StartAsync(preferredPort: 18906);
        server.Register(job);

        using var client = new HttpClient();
        var host = server.LanIp ?? "localhost";
        var url = Uri.EscapeDataString($"http://{host}:{server.Port}/s/{job.Token}");
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/qr.png?u={url}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("image/png", resp.Content.Headers.ContentType?.ToString());
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], body[..4]); // PNG 魔数
    }

    [Fact]
    public async Task 二维码_外站URL与错误端口_均400()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "f2");
        await using var server = await SessionServer.StartAsync(preferredPort: 18911);
        server.Register(job);

        using var client = new HttpClient();
        // 外站 host → 拒绝（防止持 token 者生成钓鱼二维码）
        var evil = Uri.EscapeDataString($"http://evil.example.com:{server.Port}/s/{job.Token}");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/qr.png?u={evil}")).StatusCode);
        // 端口不匹配 → 拒绝
        var wrongPort = Uri.EscapeDataString($"http://localhost:9999/s/{job.Token}");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/qr.png?u={wrongPort}")).StatusCode);
        // 非 http(s) scheme → 拒绝
        var js = Uri.EscapeDataString($"javascript:alert(1)");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/qr.png?u={js}")).StatusCode);
    }

    [Fact]
    public async Task 非法二维码参数_返回400()
    {
        using var dir = new TempDir();
        var job = ServingJob(dir.Path, "g1");
        await using var server = await SessionServer.StartAsync(preferredPort: 18907);
        server.Register(job);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/qr.png?u=javascript:alert(1)");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task 字幕_按流序号提供WebVTT()
    {
        using var dir = new TempDir();
        var vtt = Path.Combine(dir.Path, "2.vtt");
        File.WriteAllText(vtt, "WEBVTT\n\n00:00.000 --> 00:01.000\n你好\n");
        var job = ServingJob(dir.Path, "h1", subtitleVtt: vtt);
        await using var server = await SessionServer.StartAsync(preferredPort: 18908);
        server.Register(job);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/sub/2");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("text/vtt", resp.Content.Headers.ContentType?.ToString());
        Assert.StartsWith("WEBVTT", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 未知Token_页面与媒体均404()
    {
        using var dir = new TempDir();
        await using var server = await SessionServer.StartAsync(preferredPort: 18909);

        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"http://localhost:{server.Port}/s/00000000000000000000000000000000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"http://localhost:{server.Port}/s/00000000000000000000000000000000/media")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"http://localhost:{server.Port}/s/00000000000000000000000000000000/status")).StatusCode);
    }

    [Fact]
    public async Task 转码中的中央缓存条目_受LRU保护_就绪无传输不保护()
    {
        var cacheRoot = Path.Combine(TestEnv.NewTestDir(), "cache");
        var old = Environment.GetEnvironmentVariable("PU_CACHE_DIR");
        Environment.SetEnvironmentVariable("PU_CACHE_DIR", cacheRoot);
        try
        {
            await using var server = await SessionServer.StartAsync(preferredPort: 18912);
            var entry = Path.Combine(cacheRoot, "abcdef");
            Directory.CreateDirectory(Path.Combine(entry, "out.mp4.hls.tmp")); // 生产中的临时目录
            var job = new MediaJob
            {
                Token = "p1",
                SourcePath = "x.mkv",
                Title = "x",
                SourceDescription = "d",
                ArtifactPath = Path.Combine(entry, "out.mp4.hls", "index.m3u8"),
                ContentType = "video/mp4",
                PlanExplanation = "e",
                IsHls = true,
            }; // 不 SetServing → 保持 Transcoding
            server.Register(job);

            // 转码中（.tmp 正在写入）的条目必须受保护，不能被 LRU 删
            Assert.Contains(entry, server.ProtectedEntryDirs());

            // 就绪且从未播放 → 放行给 LRU（文件夹会话看过的每一集不能永久占住 20GB）
            job.SetServing([]);
            Assert.DoesNotContain(entry, server.ProtectedEntryDirs());

            // 传输（播放中）→ 窗口内保护
            server.BeginTransfer(job.Token);
            Assert.Contains(entry, server.ProtectedEntryDirs());

            // 窗口过期后放行（模拟播放停止一段时间）
            var oldWindow = SessionServer.TransferProtectWindow;
            SessionServer.TransferProtectWindow = TimeSpan.Zero;
            try
            {
                Assert.DoesNotContain(entry, server.ProtectedEntryDirs());
            }
            finally
            {
                SessionServer.TransferProtectWindow = oldWindow;
            }

            // 失败任务永不保护
            job.SetFailed("boom");
            Assert.DoesNotContain(entry, server.ProtectedEntryDirs());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CACHE_DIR", old);
        }
    }

    [Fact]
    public async Task 直出任务_字幕后补中的缓存条目_受LRU保护()
    {
        var cacheRoot = Path.Combine(TestEnv.NewTestDir(), "cache");
        var old = Environment.GetEnvironmentVariable("PU_CACHE_DIR");
        Environment.SetEnvironmentVariable("PU_CACHE_DIR", cacheRoot);
        try
        {
            await using var server = await SessionServer.StartAsync(preferredPort: 18926);
            // 直出（ServeOriginal）：产物 = 源文件本身（不在缓存），字幕写入中央缓存 {源指纹}/subs
            var src = Path.Combine(TestEnv.NewTestDir(), "movie.mp4");
            File.WriteAllBytes(src, new byte[64]);
            var job = new MediaJob
            {
                Token = "p2",
                SourcePath = src,
                Title = "m",
                SourceDescription = "d",
                ArtifactPath = src, // 直出：产物即源文件
                ContentType = "video/mp4",
                PlanExplanation = "e",
            };
            job.SetServing(); // 直出立即置可播，字幕仍在后台抽取 → SubtitlesPending = true
            server.Register(job);

            // 字幕还没抽完：源指纹对应的缓存条目（{root}/{key}，subs 写在这）必须受保护
            var subsEntry = CacheKey.ArtifactDirFor(src);
            Assert.True(job.SubtitlesPending);
            Assert.Contains(subsEntry, server.ProtectedEntryDirs());

            // 字幕定案后：不再保护（条目可被 LRU 淘汰）
            job.SetSubtitles([]);
            Assert.DoesNotContain(subsEntry, server.ProtectedEntryDirs());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CACHE_DIR", old);
        }
    }

    [Fact]
    public async Task HLS分片路由_只提供播放列表与分片_清单不外泄()
    {
        using var dir = new TempDir();
        var hlsDir = Path.Combine(dir.Path, "out.mp4.hls");
        Directory.CreateDirectory(hlsDir);
        File.WriteAllText(Path.Combine(hlsDir, "index.m3u8"), "#EXTM3U\nseg_00001.ts\n");
        File.WriteAllText(Path.Combine(hlsDir, "seg_00001.ts"), "ts-data");
        // 复用清单（历史版本含源路径）：绝不能通过 /hls/ 下载
        File.WriteAllText(Path.Combine(hlsDir, "index.m3u8.json"),
            "{\"SourcePath\":\"D:\\\\secret\\\\movie.mkv\"}");
        var job = new MediaJob
        {
            Token = "hls1",
            SourcePath = "x.mkv",
            Title = "x",
            SourceDescription = "d",
            ArtifactPath = Path.Combine(hlsDir, "index.m3u8"),
            ContentType = "application/vnd.apple.mpegurl",
            PlanExplanation = "e",
            IsHls = true,
        };
        job.SetServing([]);
        await using var server = await SessionServer.StartAsync(preferredPort: 18913);
        server.Register(job);

        using var client = new HttpClient();
        // 播放列表与 TS 分片正常提供
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/hls/index.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/hls/seg_00001.ts")).StatusCode);
        // 清单 / 其它扩展名一律 404，路径不泄露
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/hls/index.m3u8.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/hls/evil.txt")).StatusCode);
    }

    [Fact]
    public async Task 端口被占_向上探测()
    {
        using var dir = new TempDir();
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 18910);
        blocker.Start();
        try
        {
            await using var server = await SessionServer.StartAsync(preferredPort: 18910);
            Assert.True(server.Port > 18910);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task 字幕后补期间条目受LRU保护_定案后放行()
    {
        var cacheRoot = Path.Combine(TestEnv.NewTestDir(), "cache");
        var old = Environment.GetEnvironmentVariable("PU_CACHE_DIR");
        Environment.SetEnvironmentVariable("PU_CACHE_DIR", cacheRoot);
        try
        {
            await using var server = await SessionServer.StartAsync(preferredPort: 18914);
            var entry = Path.Combine(cacheRoot, "deadbeef");
            var job = new MediaJob
            {
                Token = "p2",
                SourcePath = "y.mp4",
                Title = "y",
                SourceDescription = "d",
                ArtifactPath = Path.Combine(entry, "out.mp4.hls", "index.m3u8"),
                ContentType = "video/mp4",
                PlanExplanation = "e",
                IsHls = true,
            };
            job.SetServing(); // 直出/复用命中：视频已可播，但字幕还在后台抽
            server.Register(job);

            // 字幕未定案：{key} 条目（subs 正在往里写）必须受保护，不能被 LRU 删
            Assert.True(job.SubtitlesPending);
            Assert.Contains(entry, server.ProtectedEntryDirs());

            // 字幕定案（空表也算）→ 放行给 LRU
            job.SetSubtitles([]);
            Assert.False(job.SubtitlesPending);
            Assert.DoesNotContain(entry, server.ProtectedEntryDirs());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CACHE_DIR", old);
        }
    }

    [Fact]
    public async Task 复用命中_产物被删后重新生产_不误复用()
    {
        if (!TestEnv.HasFfmpeg) return;
        // 隔离中央缓存：防测试触发淘汰删到真实缓存
        var cacheRoot = Path.Combine(TestEnv.NewTestDir(), "cache");
        var old = Environment.GetEnvironmentVariable("PU_CACHE_DIR");
        Environment.SetEnvironmentVariable("PU_CACHE_DIR", cacheRoot);
        try
        {
            using var dir = new TempDir();
            // 无 faststart → Remux 分支 → 会产出真实产物文件
            var src = await MakeVideo(dir.Path, "src.mp4",
                ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

            await using var server = await SessionServer.StartAsync(preferredPort: 18915);

            var first = await server.SubmitAsync(src);
            await WaitServingAsync(first, TimeSpan.FromSeconds(60));
            Assert.Equal(JobState.Serving, first.State);
            Assert.True(File.Exists(first.ArtifactPath), "产物应已落位");

            // 模拟产物被 LRU 淘汰 / 用户删除（连带复用清单）
            File.Delete(first.ArtifactPath);
            try { File.Delete(first.ArtifactPath + ".json"); } catch { }

            // 同源再提交：内存里旧 job 还在，但产物没了 → 必须重新生产而不是复用
            var second = await server.SubmitAsync(src);
            Assert.NotEqual(first.Token, second.Token);
            await WaitServingAsync(second, TimeSpan.FromSeconds(60));
            Assert.True(File.Exists(second.ArtifactPath), "应重新生产产物");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CACHE_DIR", old);
        }
    }

    private static async Task WaitServingAsync(MediaJob job, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<MediaJob> handler = _ => { if (job.State == JobState.Serving) tcs.TrySetResult(); };
        job.Changed += handler;
        try
        {
            if (job.State == JobState.Serving) return;
            await tcs.Task.WaitAsync(timeout);
        }
        finally
        {
            job.Changed -= handler;
        }
    }

    // ── RegisterInFlight 在途登记清理 ──

    [Fact]
    public async Task 在途登记_同步fault的任务_入表后立即移除()
    {
        var table = new ConcurrentDictionary<string, Lazy<Task<int>>>(StringComparer.Ordinal);
        // 工厂返回已 fault 的任务（等价于 ffmpeg 缺失时 SubmitCoreAsync 在首个 await 前
        // 同步 fault）：若清理续延在 GetOrAdd 工厂内注册，会先于条目入表执行 TryRemove，
        // 条目永久残留，后续提交永远命中缓存任务（装好 ffmpeg 也要重启才恢复）。
        var lazy = SessionServer.RegisterInFlight(table, "k",
            () => Task.FromException<int>(new InvalidOperationException("sync fault")));
        Assert.Empty(table); // GetOrAdd 返回后即应被清理
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => lazy.Value);
    }

    [Fact]
    public async Task 在途登记_异步完成后移除_下次提交走新工厂()
    {
        var table = new ConcurrentDictionary<string, Lazy<Task<int>>>(StringComparer.Ordinal);
        var lazy = SessionServer.RegisterInFlight(table, "k",
            async () => { await Task.Delay(30); return 42; });
        Assert.Single(table); // 任务在途：登记保留（并发提交去重）
        Assert.Equal(42, await lazy.Value);
        for (var i = 0; i < 100 && !table.IsEmpty; i++)
            await Task.Delay(10);
        Assert.Empty(table); // 完成后摘除：下一次提交走新工厂（重新探测）
    }

    [Fact]
    public async Task Mp3文件_直出_以AudioMpeg提供()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var mp3 = Path.Combine(dir.Path, "song.mp3");
        var gen = await ProcessRunner.RunAsync("ffmpeg",
        ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
         "-c:a", "libmp3lame", "-b:a", "64k", mp3]);
        Assert.True(gen.ExitCode == 0, $"ffmpeg 生成 mp3 失败: {gen.StdErr}");

        await using var server = await SessionServer.StartAsync(preferredPort: 18941);
        var job = await server.SubmitAsync(mp3);
        Assert.Equal(JobState.Serving, job.State); // 直出：立即可播
        Assert.False(job.IsHls);
        Assert.Equal("audio/mpeg", job.ContentType);

        using var http = new HttpClient();
        using var resp = await http.GetAsync($"http://127.0.0.1:{server.Port}/s/{job.Token}/media");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("audio/mpeg", resp.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<string> MakeVideo(string dir, string name, string[] codecArgs)
    {
        var path = Path.Combine(dir, name);
        var args = new List<string>
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=128x72:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
        };
        args.AddRange(codecArgs);
        args.Add(path);
        var r = await ProcessRunner.RunAsync("ffmpeg", args);
        Assert.True(r.ExitCode == 0, $"ffmpeg 生成样本失败: {r.StdErr}");
        return path;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
