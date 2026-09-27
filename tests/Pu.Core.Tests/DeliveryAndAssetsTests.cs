using System.Net;
using System.Text.Json;
using Pu.Core.Common;
using Pu.Core.Serving;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>界面改版配套的服务端能力：公共资源白名单 + ETag、扫码送达记录、播放页的前后集。
/// 全部不依赖 ffmpeg（job 手工构造后 Register）。</summary>
public class DeliveryAndAssetsTests
{
    private static MediaJob ServingJob(string path, string token)
    {
        File.WriteAllBytes(path, new byte[256]);
        var job = new MediaJob
        {
            Token = token,
            SourcePath = path,
            Title = Path.GetFileNameWithoutExtension(path),
            SourceDescription = "h264",
            ArtifactPath = path,
            ContentType = "video/mp4",
            PlanExplanation = "原样直出",
        };
        job.SetServing([]);
        return job;
    }

    [Theory]
    [InlineData("pu.css", "text/css")]
    [InlineData("pu.js", "application/javascript")]
    [InlineData("mascot.svg", "image/svg+xml")]
    [InlineData("words.json", "application/json")]
    public async Task 公共资源_白名单内可取_带ETag_再次请求304(string name, string mediaType)
    {
        await using var server = await SessionServer.StartAsync(preferredPort: 18950);
        using var client = new HttpClient();
        var url = $"http://localhost:{server.Port}/assets/{name}";

        var first = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(mediaType, first.Content.Headers.ContentType?.MediaType);
        // 页面自己的资源随版本变：必须协商缓存，不能长缓存（升级后拿到旧样式）
        Assert.True(first.Headers.CacheControl?.NoCache);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var again = new HttpRequestMessage(HttpMethod.Get, url);
        again.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("web.index.html")]
    [InlineData("..%2Fweb.index.html")]
    [InlineData("nope.js")]
    public async Task 公共资源_白名单外一律404(string name)
    {
        await using var server = await SessionServer.StartAsync(preferredPort: 18951);
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/assets/{name}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData("s")]
    [InlineData("f")]
    public async Task 失效链接_仍回页面本身_状态码404(string kind)
    {
        await using var server = await SessionServer.StartAsync(preferredPort: 18957);
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/{kind}/DEADBEEF");
        // 扫了旧二维码的人看到「链接失效了，回电脑上重新右键」，而不是浏览器空白错误页
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("text/html", resp.Content.Headers.ContentType?.ToString());
        Assert.Contains("噗~噗噗~~噗噗噗噗~~~~", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 吉祥物SVG_含全部五种表情()
    {
        await using var server = await SessionServer.StartAsync(preferredPort: 18952);
        using var client = new HttpClient();
        var svg = await client.GetStringAsync($"http://localhost:{server.Port}/assets/mascot.svg");
        foreach (var face in new[] { "idle", "busy", "ready", "error", "empty" })
            Assert.Contains($"m-{face}", svg);
    }

    [Fact]
    public void 夸夸词_合法JSON且各分组非空()
    {
        using var doc = JsonDocument.Parse(EmbeddedWeb.WordsJson);
        foreach (var group in new[] { "praise", "busy", "ready", "delivered" })
            Assert.True(doc.RootElement.GetProperty(group).GetArrayLength() > 0, group);
    }

    [Fact]
    public async Task 送达_同一设备只报一次_设备名优先用页面提示()
    {
        using var dir = new TempDir();
        await using var server = await SessionServer.StartAsync(preferredPort: 18953);
        var job = server.Register(ServingJob(Path.Combine(dir.Path, "a.mp4"), "deliv1"));
        var arrivals = new List<ClientArrival>();
        server.ClientArrived += a => { lock (arrivals) arrivals.Add(a); };

        SessionServer.CountLocalClients = true;
        try
        {
            using var client = new HttpClient();
            // iPadOS Safari 的 UA 是 Macintosh：只有页面提示能认出 iPad
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Safari/605.1.15");
            await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/status?d=ipad");
            await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/status?d=ipad");
            await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/status");
        }
        finally
        {
            SessionServer.CountLocalClients = false;
        }

        var only = Assert.Single(arrivals);
        Assert.Equal(job.Token, only.Token);
        Assert.Equal("iPad", only.Device);
        Assert.Equal(["iPad"], job.Clients.Devices);
    }

    [Fact]
    public async Task 送达_本机请求不算()
    {
        using var dir = new TempDir();
        await using var server = await SessionServer.StartAsync(preferredPort: 18954);
        var job = server.Register(ServingJob(Path.Combine(dir.Path, "a.mp4"), "deliv2"));
        var fired = false;
        server.ClientArrived += _ => fired = true;

        using var client = new HttpClient();
        await client.GetAsync($"http://localhost:{server.Port}/s/{job.Token}/status?d=iphone");

        Assert.False(fired);
        Assert.False(job.Clients.Any);
    }

    [Theory]
    [InlineData("iphone", null, "iPhone")]
    [InlineData("android-tablet", null, "安卓平板")]
    [InlineData("<script>", "Mozilla/5.0 (Linux; Android 14; Pixel 8) Mobile Safari", "安卓手机")]
    [InlineData(null, "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)", "iPhone")]
    [InlineData(null, "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Windows 电脑")]
    [InlineData(null, null, "浏览器")]
    public void 设备名_提示只认白名单_否则按UA粗判(string? hint, string? ua, string expected)
        => Assert.Equal(expected, ClientDevice.Label(hint, ua));

    [Fact]
    public void 送达记录_按IP去重_上限8台()
    {
        var log = new ClientLog();
        Assert.True(log.TryAdd("192.168.1.2", "iPad"));
        Assert.False(log.TryAdd("192.168.1.2", "iPad"));
        for (var i = 3; i < 20; i++) log.TryAdd($"192.168.1.{i}", "iPhone");
        Assert.False(log.TryAdd("192.168.1.99", "Mac"));
        Assert.Equal(["iPad", "iPhone"], log.Devices);
    }

    [Fact]
    public async Task 播放页状态_从文件夹点开时带前后集()
    {
        using var dir = new TempDir();
        var paths = new[] { "E01.mp4", "E02.mp4", "E03.mp4" }
            .Select(n => Path.Combine(dir.Path, n)).ToArray();
        foreach (var p in paths) File.WriteAllBytes(p, new byte[16]);

        await using var server = await SessionServer.StartAsync(preferredPort: 18955);
        var folder = await server.SubmitFolderAsync(dir.Path);
        // 模拟“从列表点开第 2 集”（真实路径走 OpenFolderFileAsync，需要 ffmpeg 探测）
        var middle = server.Register(ServingJob(paths[1], "folderlink1"));
        folder.MarkOpened(1, middle.Token);
        middle.LinkFolder(folder.Token);
        using var soloDir = new TempDir();
        var single = server.Register(ServingJob(Path.Combine(soloDir.Path, "solo.mp4"), "solo1"));

        using var client = new HttpClient();
        using var doc = JsonDocument.Parse(
            await client.GetStringAsync($"http://localhost:{server.Port}/s/{middle.Token}/status"));
        var link = doc.RootElement.GetProperty("folder");
        Assert.Equal(folder.Token, link.GetProperty("token").GetString());
        Assert.Equal(1, link.GetProperty("position").GetInt32());
        Assert.Equal(3, link.GetProperty("count").GetInt32());
        Assert.Equal("E01.mp4", link.GetProperty("prev").GetProperty("name").GetString());
        Assert.Equal("new", link.GetProperty("prev").GetProperty("state").GetString());
        Assert.Equal("E03.mp4", link.GetProperty("next").GetProperty("name").GetString());

        // 单文件右键：没有所属文件夹
        using var soloDoc = JsonDocument.Parse(
            await client.GetStringAsync($"http://localhost:{server.Port}/s/{single.Token}/status"));
        Assert.Equal(JsonValueKind.Null, soloDoc.RootElement.GetProperty("folder").ValueKind);
    }

    [Fact]
    public async Task 播放页状态_首集没有上一集_末集没有下一集()
    {
        using var dir = new TempDir();
        var paths = new[] { "A1.mkv", "A2.mkv" }.Select(n => Path.Combine(dir.Path, n)).ToArray();
        foreach (var p in paths) File.WriteAllBytes(p, new byte[16]);

        await using var server = await SessionServer.StartAsync(preferredPort: 18956);
        var folder = await server.SubmitFolderAsync(dir.Path);
        var first = server.Register(ServingJob(paths[0], "edge1"));
        first.LinkFolder(folder.Token);
        var last = server.Register(ServingJob(paths[1], "edge2"));
        last.LinkFolder(folder.Token);

        using var client = new HttpClient();
        using var a = JsonDocument.Parse(await client.GetStringAsync($"http://localhost:{server.Port}/s/edge1/status"));
        Assert.Equal(JsonValueKind.Null, a.RootElement.GetProperty("folder").GetProperty("prev").ValueKind);
        Assert.Equal("A2.mkv", a.RootElement.GetProperty("folder").GetProperty("next").GetProperty("name").GetString());
        using var b = JsonDocument.Parse(await client.GetStringAsync($"http://localhost:{server.Port}/s/edge2/status"));
        Assert.Equal(JsonValueKind.Null, b.RootElement.GetProperty("folder").GetProperty("next").ValueKind);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
