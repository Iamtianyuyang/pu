using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Pu.App.Shell;
using Pu.App.Tray;
using Pu.Core.Cache;
using Pu.Core.Ipc;
using Pu.Core.Serving;

namespace Pu.App;

public static class Program
{
    private const string MutexName = @"Local\pu~";

    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向环境忽略 */ }

        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?" or "help")
        {
            PrintUsage();
            return 0;
        }
        switch (args[0])
        {
            case "--version":
                Console.WriteLine("pu~ 0.3.0 (M3)");
                return 0;
            case "--register":
                RegisterShell();
                return 0;
            case "--unregister":
                UnregisterShell();
                return 0;
            case "--clean":
                CleanCache();
                return 0;
        }

        var input = Path.GetFullPath(args[0]);
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            Console.Error.WriteLine($"找不到文件或文件夹: {input}");
            return 2;
        }
        var noBrowser = args.Contains("--no-browser", StringComparer.Ordinal);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ui = new object();

        // ── 单实例：已有实例在跑 → 命名管道把路径递过去（方案.md 第六节）──
        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            Console.WriteLine("pu~ 已在运行，把任务交给已有实例…");
            var sent = await IpcHub.SendAsync(input);
            if (!sent) Console.Error.WriteLine("交付失败：已有实例可能正在退出，请稍后重试。");
            return sent ? 0 : 1;
        }

        try
        {
            // ── 本实例成为服务端 ──
            var inbox = Channel.CreateUnbounded<string>();
            var ipcTask = IpcHub.ServeAsync(inbox, cts.Token);

            var server = await SessionServer.StartAsync(ct: cts.Token);
            var url = await HandleIncomingAsync(server, input, cts.Token, ui);

            var tray = StartTray(server, cts);
            var inboxTask = Task.Run(async () =>
            {
                await foreach (var path in inbox.Reader.ReadAllAsync(cts.Token))
                {
                    try
                    {
                        var u = await HandleIncomingAsync(server, path, cts.Token, ui);
                        OpenBrowser(u);
                    }
                    catch (Exception ex)
                    {
                        lock (ui) Console.Error.WriteLine($"处理 {path} 失败: {ex.Message}");
                    }
                }
            });
            var idleTask = IdleWatchAsync(server, cts, ui);

            if (!noBrowser) OpenBrowser(url);
            Console.WriteLine();
            Console.WriteLine("pu~ 运行中 —— 右键其它视频/文件夹会直接送过来；托盘图标可停止服务。");
            Console.WriteLine("按 Ctrl+C 退出。");

            try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
            catch (OperationCanceledException) { /* Ctrl+C / 托盘停止 / 空闲超时 */ }

            cts.Cancel();
            await server.StopAsync();
            tray?.Dispose();
            try { await inboxTask; } catch { }
            try { await idleTask; } catch { }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    /// <summary>处理一个入站路径（文件或文件夹）：探测/扫描 → 注册会话 → 打印 → 返回状态页 URL。</summary>
    private static async Task<string> HandleIncomingAsync(
        SessionServer server, string path, CancellationToken ct, object ui)
    {
        lock (ui) Console.WriteLine();
        if (Directory.Exists(path))
        {
            var folder = await server.SubmitFolderAsync(path, ct);
            lock (ui)
            {
                Console.WriteLine($"pu~ 分析文件夹 {path}");
                Console.WriteLine($"  {folder.Files.Count} 个媒体文件（点开才转码）");
                Console.WriteLine($"  列表页: {server.UrlForFolder(folder)}");
            }
            return server.UrlForFolder(folder);
        }

        var job = await server.SubmitAsync(path, ct);
        lock (ui)
        {
            Console.WriteLine($"pu~ 分析 {path}");
            Console.WriteLine($"  {job.SourceDescription}");
            Console.WriteLine($"  计划: {job.PlanExplanation}");
            Console.WriteLine($"  状态页: {server.UrlFor(job)}");
        }
        job.Changed += j => PrintJobProgress(j, ui);
        return server.UrlFor(job);
    }

    private static void PrintJobProgress(MediaJob job, object ui)
    {
        lock (ui)
        {
            switch (job.State)
            {
                case JobState.Transcoding:
                    Console.Write($"\r  转码中 {job.Progress * 100:F0}%    ");
                    break;
                case JobState.Serving:
                    Console.Write("\r" + new string(' ', 36) + "\r");
                    Console.WriteLine($"  ✓ 就绪：{job.Title}（{job.PlanExplanation}）");
                    break;
                case JobState.Failed:
                    Console.Write("\r" + new string(' ', 36) + "\r");
                    Console.Error.WriteLine($"  ✗ 失败：{job.Error}");
                    break;
            }
        }
    }

    private static TrayIcon? StartTray(SessionServer server, CancellationTokenSource cts)
    {
        try
        {
            // 窗口必须在托盘线程上创建：线程池线程被回收时窗口会被销毁
            var ready = new ManualResetEventSlim();
            TrayIcon? instance = null;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    instance = new TrayIcon();
                    ready.Set();
                    instance.Run();
                }
                catch (Exception ex)
                {
                    error = ex;
                    ready.Set();
                }
            })
            { IsBackground = true, Name = "pu-tray" };
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            if (error is not null) throw error;
            if (instance is null) throw new InvalidOperationException("托盘启动超时");

            instance.OpenStatusPage += () =>
            {
                if (server.LatestUrl is { } u) OpenBrowser(u);
            };
            instance.ExitRequested += () => cts.Cancel();
            return instance;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"托盘启动失败（服务继续运行）: {ex.Message}");
            return null;
        }
    }

    private static async Task IdleWatchAsync(SessionServer server, CancellationTokenSource cts, object ui)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cts.Token);
                if (server.SessionCount == 0 && server.IdleFor > SessionServer.IdleTimeout)
                {
                    lock (ui) Console.WriteLine("空闲超时（30 分钟无会话），自动退出");
                    cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void RegisterShell()
    {
        var exts = ShellConfig.Load();
        ShellRegister.Register(exts);
        Console.WriteLine($"已注册右键菜单：{exts.Count} 个扩展名 + 文件夹（HKCU，无需管理员）。");
        Console.WriteLine("  现在可以右键任意视频/文件夹 → pu~");
        Console.WriteLine("  撤销: pu --unregister");
    }

    private static void UnregisterShell()
    {
        var exts = ShellConfig.Load();
        ShellRegister.Unregister(exts);
        Console.WriteLine($"已移除右键菜单（{exts.Count} 个扩展名 + 文件夹）。");
    }

    private static void CleanCache()
    {
        var (entries, bytes) = CacheManager.Stats();
        var freed = CacheManager.Clean();
        Console.WriteLine($"已清空缓存：{entries} 项 / {FormatSize(bytes)}（释放 {FormatSize(freed)}）。");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        _ => $"{bytes} B",
    };

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"打开浏览器失败: {ex.Message}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            pu~ —— 右键视频，扫码即播（M3：缓存 + 硬件加速 + 文件夹列表页）

            用法:
              pu --register           注册右键菜单（媒体扩展名 + 文件夹，HKCU）
              pu --unregister         移除右键菜单
              pu --clean              清空转码缓存
              pu <视频文件|文件夹>     处理并弹出状态页/列表页（已有实例则交给它）
              pu --no-browser         不自动打开浏览器
              pu --help               显示本帮助

            状态页二维码在转码开始的瞬间就给出，转完自动起播。
            缓存默认上限 20 GB，LRU 自动淘汰；空闲 30 分钟自动退出。
            """);
    }
}
