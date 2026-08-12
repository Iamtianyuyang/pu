using System.IO.Pipes;
using System.Threading.Channels;
using Pu.Core.Ipc;
using Xunit;

namespace Pu.Core.Tests;

public class IpcTests
{
    [Fact]
    public async Task 管道被其他实例占用_退避重试_释放后恢复接收()
    {
        var inbox = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // 模拟另一个实例先占住单实例命名管道（第二个服务端创建即失败 → 进入退避）
        await using var blocker = new NamedPipeServerStream(
            IpcHub.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var server = IpcHub.ServeAsync(inbox, cts.Token);
        await Task.Delay(150); // 让 ServeAsync 至少撞上一次“管道被占”

        await blocker.DisposeAsync(); // 释放管道 → 退避到期后 ServeAsync 重新取得监听权

        var deadline = Environment.TickCount64 + 8000;
        var sent = false;
        while (Environment.TickCount64 <= deadline && !sent)
            sent = await IpcHub.SendAsync("recovered", timeoutMs: 400);
        if (!sent)
        {
            cts.Cancel();
            await server;
            Assert.Fail("管道释放后 ServeAsync 未在退避窗口内恢复监听");
        }
        var got = await inbox.Reader.ReadAsync(cts.Token);
        Assert.Equal("recovered", got);

        cts.Cancel();
        await server; // 取消后应正常返回
    }

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
