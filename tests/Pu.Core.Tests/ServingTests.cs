using System.Net;
using System.Net.Http.Headers;
using Pu.Core.Serving;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>Kestrel 会话服务测试：Range 请求（Safari 拖进度条必需）。</summary>
public class ServingTests
{
    [Fact]
    public async Task Range请求_返回206与正确字节段()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sample.mp4");
        var bytes = new byte[1000];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        await using var server = await SessionServer.StartAsync(preferredPort: 18900);
        var session = server.Register(path, "video/mp4", "sample");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Range = new RangeHeaderValue(100, 199);
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{session.Token}");

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, body.Length);
        Assert.Equal(bytes[100..200], body);
        Assert.NotNull(resp.Content.Headers.ContentRange);
    }

    [Fact]
    public async Task 整文件请求_返回200与AcceptRanges()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sample.mp4");
        File.WriteAllBytes(path, new byte[64]);

        await using var server = await SessionServer.StartAsync(preferredPort: 18901);
        var session = server.Register(path, "video/mp4", "sample");

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/{session.Token}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("bytes", resp.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public async Task 未知Token_返回404()
    {
        using var dir = new TempDir();
        await using var server = await SessionServer.StartAsync(preferredPort: 18902);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{server.Port}/s/00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task 端口被占_向上探测()
    {
        using var dir = new TempDir();
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 18903);
        blocker.Start();
        try
        {
            await using var server = await SessionServer.StartAsync(preferredPort: 18903);
            Assert.True(server.Port > 18903);
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
