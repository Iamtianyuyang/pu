using System.Diagnostics;
using System.Text;

namespace Pu.Core.Common;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>外部进程执行助手：捕获 stdout/stderr，支持逐行回调与取消（取消时杀进程树）。</summary>
public static class ProcessRunner
{
    /// <summary>
    /// 执行外部进程：捕获 stdout/stderr，支持逐行回调与取消（取消时杀进程树）。
    /// maxStdoutLines / maxStderrLines：只保留最近 N 行（环形缓冲）。
    /// ffmpeg 的 -progress pipe:1 每帧一行，2 小时电影 ~20–40 万行，全量累积会吃掉 20–30MB——
    /// 进度解析走逐行回调，累积列表只供事后取尾部错误信息，不需要全量。
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default,
        int maxStdoutLines = int.MaxValue,
        int maxStderrLines = int.MaxValue)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程: {executable}");

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, onStdoutLine, cancellationToken, maxStdoutLines);
        var stderrTask = ReadLinesAsync(process.StandardError, stderrLines, onStderrLine, cancellationToken, maxStderrLines);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 进程已退出 */ }
            // 读取任务会随取消令牌一并结束，它们抛出的取消异常不该成为未观察异常
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, string.Join('\n', stdoutLines), string.Join('\n', stderrLines));
    }

    /// <summary>逐行读取；超过 maxLines 后环形覆盖，保留最近 maxLines 行（maxLines ≤ 0 不保留）。</summary>
    private static async Task ReadLinesAsync(
        StreamReader reader, List<string> sink, Action<string>? onLine, CancellationToken ct, int maxLines)
    {
        var total = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            total++;
            onLine?.Invoke(line);
            if (maxLines <= 0) continue;
            if (sink.Count < maxLines) { sink.Add(line); continue; }
            sink[(total - 1) % maxLines] = line;
        }
        // 环形回绕后把顺序修正好：最早保留的行应在列表头部（maxLines=0 时 sink 恒空，跳过）
        if (maxLines > 0 && sink.Count == maxLines && total > maxLines)
        {
            var shift = total % maxLines;
            if (shift != 0)
            {
                var head = sink.GetRange(0, shift);
                sink.RemoveRange(0, shift);
                sink.AddRange(head);
            }
        }
    }
}
