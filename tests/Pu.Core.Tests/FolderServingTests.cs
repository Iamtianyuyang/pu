using System.Net;
using System.Text.Json;
using Pu.Core.Cache;
using Pu.Core.Common;
using Pu.Core.Serving;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>文件夹模式端到端：列表页 / 状态轮询 / 点开文件 → 状态页可播。</summary>
public class FolderServingTests
{
    [Fact]
    public async Task 文件夹_列表页_状态_点开_全链路()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var sample = CreateSampleMp4(dir.Path, "sample.mp4");
        File.WriteAllText(Path.Combine(dir.Path, "notes.txt"), "非媒体");

        await using var server = await SessionServer.StartAsync(preferredPort: 18920);
        var folder = await server.SubmitFolderAsync(dir.Path);

        using var client = new HttpClient();

        // 1. 列表页 HTML（文件名由 JS 轮询 /status 渲染，页面本身是模板）
        var page = await client.GetAsync($"http://localhost:{server.Port}/f/{folder.Token}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType?.ToString());
        Assert.Contains("噗~噗噗~~噗噗噗噗~~~~", await page.Content.ReadAsStringAsync());

        // 2. 状态 JSON：初始全 new
        var st = await client.GetAsync($"http://localhost:{server.Port}/f/{folder.Token}/status");
        using var doc = JsonDocument.Parse(await st.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("new", doc.RootElement.GetProperty("files")[0].GetProperty("state").GetString());
        Assert.Equal("sample.mp4", doc.RootElement.GetProperty("files")[0].GetProperty("name").GetString());

        // 3. 点开文件 → 返回 /s/ URL
        var open = await client.PostAsync(
            $"http://localhost:{server.Port}/f/{folder.Token}/open/0", null);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        using var openDoc = JsonDocument.Parse(await open.Content.ReadAsStringAsync());
        var mediaUrl = openDoc.RootElement.GetProperty("url").GetString();
        Assert.Contains($"/s/", mediaUrl);

        // 4. 默认策略（强制转码）下等转码完成 → 媒体可播
        var served = false;
        for (var i = 0; i < 120; i++)
        {
            var s = await client.GetAsync(mediaUrl + "/status");
            using var sd = JsonDocument.Parse(await s.Content.ReadAsStringAsync());
            var state = sd.RootElement.GetProperty("state").GetString();
            if (state == "serving") { served = true; break; }
            if (state == "failed") break;
            await Task.Delay(250);
        }
        Assert.True(served, "转码未在 30s 内完成");
        var media = await client.GetAsync(mediaUrl + "/media");
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);

        // 5. 重复点开 → 复用同一任务（URL 相同）
        var open2 = await client.PostAsync(
            $"http://localhost:{server.Port}/f/{folder.Token}/open/0", null);
        using var open2Doc = JsonDocument.Parse(await open2.Content.ReadAsStringAsync());
        Assert.Equal(mediaUrl, open2Doc.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task 同源文件重复提交_复用同一Job不重复转码()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var sample = CreateSampleMp4(dir.Path, "dedup.mp4");

        await using var server = await SessionServer.StartAsync(preferredPort: 18923);

        // 顺序重复提交 → 同一 job
        var job1 = await server.SubmitAsync(sample);
        var job2 = await server.SubmitAsync(sample);
        Assert.Same(job1, job2);

        // 并发提交 → 同一 job（探测/注册只跑一次）
        var jobs = await Task.WhenAll(server.SubmitAsync(sample), server.SubmitAsync(sample));
        Assert.Same(job1, jobs[0]);
        Assert.Same(job1, jobs[1]);

        // 失败后重新提交 → 允许重建（不复用失败 job）
        job2.SetFailed("模拟失败");
        var job3 = await server.SubmitAsync(sample);
        Assert.NotSame(job1, job3);
        Assert.Equal(JobState.Serving, job3.State);
    }

    [Fact]
    public async Task 源文件被替换_重新提交_不复用旧Job()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var sample = Path.Combine(dir.Path, "replaced.mp4");
        CreateSampleMp4(dir.Path, "replaced.mp4");

        await using var server = await SessionServer.StartAsync(preferredPort: 18924);
        var job1 = await server.SubmitAsync(sample);
        Assert.Equal(JobState.Serving, job1.State); // 直出：立即可播

        // 同一路径的源文件被替换（内容/mtime 变化）→ 指纹变 → 不得复用旧任务
        CreateSampleMp4(dir.Path, "replaced.mp4");
        var job2 = await server.SubmitAsync(sample);
        Assert.NotSame(job1, job2);
        Assert.Equal(JobState.Serving, job2.State);
    }

    [Fact]
    public async Task 运行期修改转码策略_重新提交_不复用旧Job()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var sample = CreateSampleMp4(dir.Path, "policy.mp4");

        var configDir = Path.Combine(dir.Path, "cfg");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), "{\"transcode\":\"always\"}");
        var oldConfig = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        var oldCache = Environment.GetEnvironmentVariable("PU_CACHE_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", configDir);
        Environment.SetEnvironmentVariable("PU_CACHE_DIR", Path.Combine(dir.Path, "cache"));
        try
        {
            await using var server = await SessionServer.StartAsync(preferredPort: 18925);

            // always：任何视频强制转码（gpu:*;fmt:5 变体）
            var job1 = await server.SubmitAsync(sample);
            Assert.Equal(JobState.Transcoding, job1.State);
            Assert.Contains("gpu:", job1.Variant);
            await WaitForServingAsync(job1);

            // 运行期改为 auto → 同一文件应走直出（fmt:5 变体）→ 变体不同 → 不复用旧 job
            File.WriteAllText(Path.Combine(configDir, "config.json"), "{\"transcode\":\"auto\"}");
            var job2 = await server.SubmitAsync(sample);
            Assert.NotSame(job1, job2);
            Assert.Equal("fmt:5", job2.Variant);
            Assert.Equal(JobState.Serving, job2.State);

            // 再提交（配置不变）→ 回到复用路径
            var job3 = await server.SubmitAsync(sample);
            Assert.Same(job2, job3);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", oldConfig);
            Environment.SetEnvironmentVariable("PU_CACHE_DIR", oldCache);
        }
    }

    [Fact]
    public async Task StopAsync_取消后台转码并等待退出_临时产物已清理()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        CreateSampleMp4(dir.Path, "stop.mp4", durationSeconds: 5);

        var configDir = Path.Combine(dir.Path, "cfg");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), "{\"transcode\":\"always\"}");
        var oldConfig = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", configDir);
        try
        {
            await using var server = await SessionServer.StartAsync(preferredPort: 18926);
            var job = await server.SubmitAsync(Path.Combine(dir.Path, "stop.mp4"));
            Assert.Equal(JobState.Transcoding, job.State);

            // 关闭服务：取消生命周期令牌并等待后台任务（ffmpeg）退出
            await server.StopAsync();
            Assert.Equal(0, server.ActiveJobCount);
            Assert.Equal(JobState.Failed, job.State); // 已取消

            // 半截临时产物被清理，不留 .tmp 残渣
            var puDir = Path.Combine(dir.Path, ".pu");
            if (Directory.Exists(puDir))
                Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(puDir),
                    p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", oldConfig);
        }
    }

    [Fact]
    public async Task 就地产物复用_字幕目录按源指纹隔离_不写进HLS产物目录()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();

        // 生成带内嵌 SRT 字幕的 mkv（非 MP4 容器 → Remux HLS 就地产物）
        var baseVideo = Path.Combine(dir.Path, "base.mp4");
        var make = await ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=128x72:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
            baseVideo,
        });
        Assert.True(make.ExitCode == 0, $"生成基础视频失败: {make.StdErr}");
        var srt = Path.Combine(dir.Path, "sub.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,000\n你好\n");
        var mkv = Path.Combine(dir.Path, "subbed.mkv");
        var mux = await ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error", "-i", baseVideo, "-i", srt,
            "-map", "0", "-map", "1", "-c", "copy", "-c:s", "srt",
            mkv,
        });
        Assert.True(mux.ExitCode == 0, $"封装字幕失败: {mux.StdErr}");

        await using var server1 = await SessionServer.StartAsync(preferredPort: 18927);
        var job1 = await server1.SubmitAsync(mkv);
        await WaitForServingAsync(job1);

        // 字幕按源指纹隔离在 .pu/{指纹}/subs，不在 HLS 产物目录里
        var puDir = Path.Combine(dir.Path, ".pu");
        var subsDir = Path.Combine(puDir, CacheKey.For(mkv), "subs");
        var hlsDir = Directory.GetDirectories(puDir)
            .Single(d => Path.GetFileName(d).StartsWith("subbed.mkv."));
        Assert.DoesNotContain(Directory.GetDirectories(hlsDir), d => Path.GetFileName(d) == "subs");

        // 模拟重启（新服务实例，内存去重表清空）→ 命中就地产物 → 字幕必须复用 .pu/{指纹}/subs，
        // 不得在 HLS 产物目录内重新抽取（旧 bug：artifactDir 已是产物目录还往下拼指纹）
        await using var server2 = await SessionServer.StartAsync(preferredPort: 18928);
        var job2 = await server2.SubmitAsync(mkv);
        await WaitForServingAsync(job2);

        var sub = Assert.Single(job2.Subtitles);
        Assert.StartsWith(Path.Combine(subsDir) + Path.DirectorySeparatorChar, sub.VttPath);
        Assert.False(Directory.Exists(Path.Combine(hlsDir, "subs")));
        Assert.Equal(job1.ArtifactPath, job2.ArtifactPath); // 同一产物（复用命中）
    }

    [Fact]
    public async Task 调用方取消_不连坐后台任务_注册照常完成()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var sample = CreateSampleMp4(dir.Path, "cancel.mp4");

        await using var server = await SessionServer.StartAsync(preferredPort: 18929);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 已取消的调用方立即拿到 OCE；但共享任务已启动（RegisterInFlight 工厂立即执行）
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.SubmitAsync(sample, cts.Token));

        // 后台任务绑定服务生命周期令牌，不受调用方取消影响：探测照常完成并注册 job
        for (var i = 0; i < 100 && server.LatestUrl is null; i++)
            await Task.Delay(50);
        Assert.NotNull(server.LatestUrl);
        var token = server.LatestUrl.Split('/').Last();
        for (var i = 0; i < 40 && server.JobStateFor(token) != JobState.Serving; i++)
            await Task.Delay(50);
        Assert.Equal(JobState.Serving, server.JobStateFor(token));
    }

    private static async Task WaitForServingAsync(MediaJob job)
    {
        for (var i = 0; i < 120; i++)
        {
            if (job.State == JobState.Serving) return;
            if (job.State == JobState.Failed) throw new InvalidOperationException($"转码失败: {job.Error}");
            await Task.Delay(250);
        }
        throw new TimeoutException("转码未在 30s 内完成");
    }

    /// <summary>生成 H.264+AAC faststart MP4（128×72，直出路径）。</summary>
    private static string CreateSampleMp4(string dir, string name, int durationSeconds = 1)
    {
        var sample = Path.Combine(dir, name);
        var r = Pu.Core.Common.ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", $"testsrc=duration={durationSeconds}:size=128x72:rate=10",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={durationSeconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart",
            sample,
        }).GetAwaiter().GetResult();
        Assert.True(r.ExitCode == 0, $"生成样本失败: {r.StdErr}");
        return sample;
    }

    [Fact]
    public async Task 未知文件夹Token_404()
    {
        await using var server = await SessionServer.StartAsync(preferredPort: 18921);
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/f/00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task 空文件夹_抛错()
    {
        using var dir = new TempDir();
        await using var server = await SessionServer.StartAsync(preferredPort: 18922);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SubmitFolderAsync(dir.Path));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
