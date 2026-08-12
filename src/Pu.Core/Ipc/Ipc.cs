using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;

namespace Pu.Core.Ipc;

/// <summary>
/// 单实例交付（方案.md 第六节）：新进程把文件路径经命名管道递给已在跑的实例。
/// 跨平台实现（NamedPipeStream），无 Windows API 依赖。
/// </summary>
public static class IpcHub
{
    public const string PipeName = "pu~-ipc";

    public static async Task ServeAsync(Channel<string> inbox, CancellationToken ct = default)
    {
        var pipeBusyBackoffMs = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipeBusyBackoffMs = 0; // 创建成功：管道归本进程，复位退避
            }
            catch (IOException)
            {
                // 管道已被其它实例占用（如快速用户切换的另一个会话也起了 pu~）：
                // 单实例管道创建即失败会立刻重进循环——必须退避，否则变成紧循环空转。
                // 正常单实例场景不会走到这里（崩溃进程的管道由 OS 自动回收）。
                pipeBusyBackoffMs = pipeBusyBackoffMs == 0 ? 500 : Math.Min(pipeBusyBackoffMs * 2, 5000);
                try { await Task.Delay(pipeBusyBackoffMs, ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await using (pipe)
                {
                    await pipe.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(ct);
                    if (!string.IsNullOrWhiteSpace(line))
                        await inbox.Writer.WriteAsync(line, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // 客户端异常断开 → 重开监听（管道已创建成功，无需退避）
            }
        }
    }

    public static async Task<bool> SendAsync(string message, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) return false;
            try
            {
                using var cts = new CancellationTokenSource(remaining);
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                await pipe.ConnectAsync(cts.Token);
                await using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync(message.AsMemory(), cts.Token);
                return true;
            }
            catch (Exception) when (Environment.TickCount64 < deadline)
            {
                // 已有实例刚启动、还没开始监听 → 稍后重试，避免右键“没反应”
                await Task.Delay(150);
            }
            catch
            {
                return false; // 超时 / 没有实例在跑
            }
        }
    }
}
