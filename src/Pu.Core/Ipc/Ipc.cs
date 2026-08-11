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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line))
                    await inbox.Writer.WriteAsync(line, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // 客户端异常断开 → 重开监听
            }
        }
    }

    public static async Task<bool> SendAsync(string message, int timeoutMs = 3000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(cts.Token);
            await using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync(message.AsMemory(), cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false; // 没有实例在跑 / 超时
        }
    }
}
