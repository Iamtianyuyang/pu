using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
