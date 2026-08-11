using System.Diagnostics;
using System.Text;

namespace Pu.Core.Common;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>外部进程执行助手：捕获 stdout/stderr，支持逐行回调与取消（取消时杀进程树）。</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        CancellationToken cancellationToken = default)
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
        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, onStdoutLine, cancellationToken);
        var stderrTask = ReadLinesAsync(process.StandardError, stderrLines, onStderrLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 进程已退出 */ }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, string.Join('\n', stdoutLines), string.Join('\n', stderrLines));
    }

    private static async Task ReadLinesAsync(
        StreamReader reader, List<string> sink, Action<string>? onLine, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            sink.Add(line);
            onLine?.Invoke(line);
        }
    }
}
