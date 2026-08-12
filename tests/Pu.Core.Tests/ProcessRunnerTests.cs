using Pu.Core.Common;
using Xunit;

namespace Pu.Core.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task 正常退出_返回退出码与输出()
    {
        var r = await ProcessRunner.RunAsync("cmd.exe", ["/c", "echo", "hi"], timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hi", r.StdOut);
    }

    [Fact]
    public async Task 超时_杀进程并抛TimeoutException()
    {
        // ping -n 30 约耗时 29 秒，1 秒超时下必须被终止并抛 TimeoutException
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunAsync("cmd.exe", ["/c", "ping", "-n", "30", "127.0.0.1"],
                timeout: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 调用方取消_抛OperationCanceledException而非Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync("cmd.exe", ["/c", "ping", "-n", "30", "127.0.0.1"],
                cancellationToken: cts.Token, timeout: TimeSpan.FromSeconds(30)));
    }
}
