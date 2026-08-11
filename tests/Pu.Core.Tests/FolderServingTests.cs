using System.Net;
using System.Text.Json;
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
        var sample = Path.Combine(dir.Path, "sample.mp4");
        var r = await Pu.Core.Common.ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=128x72:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart",
            sample,
        });
        Assert.True(r.ExitCode == 0, $"生成样本失败: {r.StdErr}");
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
