using System.Threading.Channels;
using Pu.Core.Ipc;
using Xunit;

namespace Pu.Core.Tests;

public class IpcTests
{
    [Fact]
    public async Task 交付路径_接收方收到()
    {
        var inbox = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = IpcHub.ServeAsync(inbox, cts.Token);

        var sent = await IpcHub.SendAsync(@"D:\movies\第一集.mkv");
        Assert.True(sent);

        var got = await inbox.Reader.ReadAsync(cts.Token);
        Assert.Equal(@"D:\movies\第一集.mkv", got);

        cts.Cancel();
        await server; // 取消后应正常返回
    }

    [Fact]
    public async Task 无实例在跑_Send失败不抛异常()
    {
        // 用一个几乎不可能被占用的名字，确保没有服务端
        var ok = await IpcHub.SendAsync("x", timeoutMs: 300);
        Assert.False(ok);
    }
}
