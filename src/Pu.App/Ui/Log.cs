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

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu", "pu.log");

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
                File.AppendAllText(FilePath, line + "\r\n");
            }
        }
        catch { /* 日志失败不影响主流程 */ }
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
