using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Pu.Core.Common;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>ffmpeg 输出文本的混合解码：UTF-8 严格优先，非法序列回退系统 OEM 代码页
/// （新版 ffmpeg 重定向输出按 UTF-8 写；老版本/终端风格输出按控制台代码页写，中文系统即 GBK——
/// 一律按 UTF-8 或一律按 GBK 都会在另一方场景把中文报错/中文文件名解成乱码）。
/// 逐行判定：GBK 双字节序列约 2/3 落在非法 UTF-8 区间，能可靠触发回退；
/// ASCII 内容（-progress / -encoders）两种解码结果一致，不受影响。</summary>
internal static class ProcessOutputEncoding
{
    private static readonly UTF8Encoding Utf8Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>解码一行原始字节：先严格 UTF-8，非法序列回退 OEM 代码页。</summary>
    public static string DecodeLine(ReadOnlySpan<byte> bytes)
    {
        try { return Utf8Strict.GetString(bytes); }
        catch (DecoderFallbackException) { return Oem.GetString(bytes); }
    }

    private static readonly Encoding Oem = DetectOem();

    private static Encoding DetectOem()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.UTF8; }
    }
}

/// <summary>外部进程执行助手：捕获 stdout/stderr，支持逐行回调与取消（取消时杀进程树）。</summary>
public static class ProcessRunner
{
    /// <summary>
    /// 执行外部进程：捕获 stdout/stderr，支持逐行回调与取消（取消时杀进程树）。
    /// maxStdoutLines / maxStderrLines：只保留最近 N 行（环形缓冲）。
    /// timeout：整次执行的最长耗时；超时杀进程并抛 TimeoutException（与调用方取消的 OCE 区分）。
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
        int maxStderrLines = int.MaxValue,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程: {executable}");

        // 超时兜底：与调用方取消令牌并联。超时触发时能靠 IsCancellationRequested 区分（见下方 catch 过滤器）
        using var timeoutCts = timeout is { } t && t > TimeSpan.Zero ? new CancellationTokenSource(t) : null;
        using var linkedCts = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var effectiveCt = linkedCts?.Token ?? cancellationToken;

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        // 直接读原始字节流逐行混合解码（不设 StandardOutputEncoding/StandardErrorEncoding）：
        // 编码判定见 ProcessOutputEncoding——两个方向（UTF-8 / OEM）都兼容
        var stdoutTask = ReadLinesAsync(process.StandardOutput.BaseStream, stdoutLines, onStdoutLine, effectiveCt, maxStdoutLines);
        var stderrTask = ReadLinesAsync(process.StandardError.BaseStream, stderrLines, onStderrLine, effectiveCt, maxStderrLines);

        try
        {
            await process.WaitForExitAsync(effectiveCt);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true }
            && !cancellationToken.IsCancellationRequested)
        {
            // 超时：杀进程树，抛 TimeoutException（调用方按各自语义处理：探测超时/编码器不可用/字幕放弃）
            try { process.Kill(entireProcessTree: true); } catch { /* 进程已退出 */ }
            // 读取任务会随令牌一并结束，它们抛出的取消异常不该成为未观察异常
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            throw new TimeoutException($"进程执行超时（{timeout!.Value.TotalSeconds:0} 秒）: {executable}");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 进程已退出 */ }
            // 读取任务会随令牌一并结束，它们抛出的取消异常不该成为未观察异常
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, string.Join('\n', stdoutLines), string.Join('\n', stderrLines));
    }

    /// <summary>逐行读取进程输出（原始字节 → 混合解码，见 ProcessOutputEncoding）。
    /// 超过 maxLines 后环形覆盖，保留最近 maxLines 行（maxLines ≤ 0 不保留——
    /// 转码进度每帧一行，2 小时电影 ~40 万行，全量累积会吃掉 20–30MB；
    /// 进度解析走逐行回调，累积列表只供事后取尾部错误信息）。</summary>
    private static async Task ReadLinesAsync(
        Stream stream, List<string> sink, Action<string>? onLine, CancellationToken ct, int maxLines)
    {
        var line = new List<byte>(256);
        var buffer = new byte[8192];
        var total = 0;
        void Flush()
        {
            if (line.Count == 0) return;
            // 与 ReadLineAsync 语义一致：剥离行尾 \r（Windows 上子进程按文本模式写 \r\n）
            if (line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
            total++;
            var text = ProcessOutputEncoding.DecodeLine(CollectionsMarshal.AsSpan(line));
            line.Clear();
            onLine?.Invoke(text);
            if (maxLines <= 0) return;
            if (sink.Count < maxLines) { sink.Add(text); return; }
            sink[(total - 1) % maxLines] = text;
        }

        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(), ct);
            if (n == 0) break;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] == (byte)'\n') Flush();
                else line.Add(buffer[i]);
            }
        }
        Flush(); // 末行无换行符

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
