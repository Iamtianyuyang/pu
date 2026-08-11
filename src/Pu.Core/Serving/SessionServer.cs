using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Pu.Core.Serving;

public sealed record MediaSession(string Token, string FilePath, string ContentType, string Title);

/// <summary>
/// Kestrel 会话服务（方案.md 第九节）：
/// - 监听 0.0.0.0（http.sys 需要管理员，Kestrel 不需要 —— 硬约束）
/// - URL 携带随机 token，未知道路的人拿不到内容
/// - Range 处理交给 Results.File，Safari 拖动进度条可用
/// </summary>
public sealed class SessionServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MediaSession> _sessions = new(StringComparer.Ordinal);
    private readonly WebApplication _app;

    public int Port { get; }
    public string? LanIp { get; }

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
            if (!server._sessions.TryGetValue(token, out var session))
                return Results.NotFound();
            return Results.File(session.FilePath, session.ContentType, enableRangeProcessing: true);
        });
        app.MapGet("/", () => Results.Text("pu~ is running"));

        await app.StartAsync(ct);
        return server;
    }

    public MediaSession Register(string filePath, string contentType, string title)
    {
        var session = new MediaSession(RandomNumberGenerator.GetHexString(16), filePath, contentType, title);
        _sessions[session.Token] = session;
        return session;
    }

    public Task StopAsync() => _app.StopAsync();
    public ValueTask DisposeAsync() => _app.DisposeAsync();

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
}
