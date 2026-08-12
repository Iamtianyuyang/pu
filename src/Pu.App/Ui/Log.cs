using Pu.Core.Common;

namespace Pu.App.Ui;

/// <summary>服务模式日志：WinExe 无控制台，关键事件写入 %LOCALAPPDATA%\Pu\pu.log（--debug 时同时输出控制台）。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static volatile bool _console;
    private static bool _dirReady;

    public static bool ConsoleOutput
    {
        get => _console;
        set => _console = value;
    }

    private static string FilePath => Path.Combine(FfmpegLocator.ConfigDir, "pu.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {level} {msg}";
        if (_console)
        {
            try { Console.WriteLine(line); } catch { }
        }
        try
        {
            lock (Gate)
            {
                EnsureDir();
                RotateIfNeeded();
                File.AppendAllText(FilePath, line + "\r\n");
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    /// <summary>日志轮转：超过 1MB 把当前文件改名 .1（旧的覆盖），防止托盘常驻几个月日志无限膨胀。</summary>
    private static void RotateIfNeeded()
    {
        const long MaxBytes = 1L << 20;
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                var old = FilePath + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(FilePath, old);
            }
        }
        catch { /* 轮转失败不阻塞写日志 */ }
    }

    /// <summary>确保 %LOCALAPPDATA%\Pu 存在：便携版全新机器上该目录可能从未创建，
    /// AppendAllText 会静默失败，所有诊断日志丢失。创建失败不置位 → 下次重试。</summary>
    private static void EnsureDir()
    {
        if (_dirReady) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _dirReady = true;
    }
}
