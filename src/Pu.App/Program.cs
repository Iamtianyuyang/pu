using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Pu.App.Shell;
using Pu.App.Tray;
using Pu.App.Ui;
using Pu.Core.Cache;
using Pu.Core.Ipc;
using Pu.Core.Serving;

namespace Pu.App;

public static class Program
{
    private const string MutexName = @"Local\pu~";
    private static volatile FolderJob? s_currentFolder;

    // 每个 job 只订阅一次 Changed（文件夹复用同一 job 时避免重复订阅/重复刷新）
    private static readonly HashSet<MediaJob> WiredJobs = [];
    private static readonly object WireGate = new();

    /// <summary>给 job 挂进度通知：同一个 job 只挂一次（文件夹重复点开复用同一 job）。</summary>
    private static void WireJob(MediaJob job, MainWindow window)
    {
        lock (WireGate)
        {
            if (!WiredJobs.Add(job)) return;
        }
        job.Changed += _ => window.SetJob(job);
    }

    public static async Task<int> Main(string[] args)
    {
        // ── 命令模式：临时分配控制台输出结果（WinExe 默认无控制台）──
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            bool needConsole = args.Length == 0
                || args[0] is "--register" or "--unregister" or "--clean" or "--install" or "--uninstall" or "--help" or "-h" or "/?" or "help" or "--version";
            if (needConsole) AllocConsole();
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try
            {
                return await RunCommandAsync(args);
            }
            catch (Exception ex)
            {
                // 命令模式也要有友好错误（如 --install 时已安装实例正在运行导致复制失败）
                Console.Error.WriteLine($"错误: {ex.Message}");
                return 1;
            }
        }

        // ── 服务模式：pu <文件|文件夹> ──
        var debug = args.Contains("--debug", StringComparer.Ordinal);
        if (debug) AllocConsole();
        Log.ConsoleOutput = debug;
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var input = Path.GetFullPath(args[0]);
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            Log.Error($"找不到文件或文件夹: {input}");
            return 2;
        }

        using var cts = new CancellationTokenSource();

        // ── 单实例：已有实例在跑 → 命名管道把路径递过去 ──
        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            Log.Info($"把任务交给已有实例: {input}");
            var sent = await IpcHub.SendAsync(input);
            return sent ? 0 : 1;
        }

        try
        {
            var inbox = Channel.CreateUnbounded<string>();
            var ipcTask = IpcHub.ServeAsync(inbox, cts.Token);

            var server = await SessionServer.StartAsync(ct: cts.Token);
            SessionServer.LogSink = Log.Info; // 请求级诊断进 pu.log
            Log.Info($"服务已启动，端口 {server.Port}");

            // WPF 主窗口（专用 STA UI 线程）
            var window = StartWindow(server, cts);
            window.CloseRequested += () => cts.Cancel();
            window.FolderFileClicked += index =>
            {
                var folder = s_currentFolder;
                if (folder is null) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 用服务生命周期令牌：客户端断开不取消，但服务停止时随 StopAsync 统一取消
                        var job = await server.OpenFolderFileAsync(folder, index, server.ShutdownToken);
                        WireJob(job, window); // 进度更新驱动窗口
                        window.ActivateJob(job);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"打开文件失败: {ex.Message}");
                        window.SetFolderFileError(index, ex.Message);
                    }
                });
            };

            var tray = StartTray(server, cts, window);

            // 先亮窗（「正在分析」占位），再跑探测/决策——避免右键后几秒没界面像卡死
            window.ShowBusy(input);
            window.ShowWindow();

            // 处理入站路径（文件或文件夹）；失败不退出——窗口显示错误，等待下一个任务
            var url = "";
            try
            {
                url = await HandleIncomingAsync(server, window, input, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 窗口关闭/空闲超时触发的取消：不是处理失败，静默
            }
            catch (Exception ex)
            {
                Log.Error($"处理 {input} 失败: {ex.Message}");
                window.ShowError(input, ex.Message);
            }
            var inboxTask = Task.Run(async () =>
            {
                await foreach (var path in inbox.Reader.ReadAllAsync(cts.Token))
                {
                    try
                    {
                        window.ShowBusy(path);
                        window.ShowWindow();
                        await HandleIncomingAsync(server, window, path, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 服务关闭中：循环即将退出，静默
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"处理 {path} 失败: {ex.Message}");
                        window.ShowError(path, ex.Message);
                    }
                }
            });
            var idleTask = IdleWatchAsync(server, cts);

            if (url.Length > 0) Log.Info($"就绪: {url}");
            window.ShowWindow();

            try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
            catch (OperationCanceledException) { /* 窗口关闭 / 托盘停止 / 空闲超时 */ }

            cts.Cancel();
            await server.StopAsync();
            tray?.Dispose();
            window.Dispose();
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
            Log.Error($"错误: {ex.Message}");
            return 1;
        }
    }

    /// <summary>处理一个入站路径：探测/扫描 → 注册会话 → 推给 WPF 窗口显示。</summary>
    private static async Task<string> HandleIncomingAsync(
        SessionServer server, MainWindow window, string path, CancellationToken ct)
    {
        if (Directory.Exists(path))
        {
            var folder = await server.SubmitFolderAsync(path, ct);
            s_currentFolder = folder;
            Log.Info($"文件夹 {path}：{folder.Files.Count} 个媒体文件 → {server.UrlForFolder(folder)}");
            window.SetFolder(folder);
            return server.UrlForFolder(folder);
        }

        s_currentFolder = null;
        window.SetFolder(null);
        var job = await server.SubmitAsync(path, ct);
        Log.Info($"分析 {path}：{job.SourceDescription}");
        Log.Info($"计划: {job.PlanExplanation}");
        Log.Info($"状态页: {server.UrlFor(job)}");
        WireJob(job, window); // 只订阅一次（文件夹复用同一 job 时不重复订阅/重复刷新）
        window.ActivateJob(job);
        return server.UrlFor(job);
    }

    private static MainWindow StartWindow(SessionServer server, CancellationTokenSource cts)
    {
        var ready = new ManualResetEventSlim();
        MainWindow? instance = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                };
                instance = new MainWindow();
                ready.Set();
                instance.Run();
            }
            catch (Exception ex)
            {
                error = ex;
                ready.Set();
                Log.Error($"WPF 窗口线程异常: {ex}");
                cts.Cancel();
            }
        })
        { IsBackground = true, Name = "pu-ui" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        if (error is not null) throw error;
        if (instance is null) throw new InvalidOperationException("主窗口启动超时");
        instance.SetBaseUrl($"http://{server.LanIp ?? "localhost"}:{server.Port}");
        instance.JobStateLookup = server.JobStateFor; // 文件夹行徽标：转码中/就绪/失败
        return instance;
    }

    private static TrayIcon? StartTray(SessionServer server, CancellationTokenSource cts, MainWindow window)
    {
        try
        {
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

            instance.ShowRequested += () => window.ShowWindow();
            instance.ExitRequested += () => cts.Cancel();
            return instance;
        }
        catch (Exception ex)
        {
            Log.Error($"托盘启动失败（服务继续运行）: {ex.Message}");
            return null;
        }
    }

    private static async Task IdleWatchAsync(SessionServer server, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cts.Token);
                if (server.ActiveJobCount == 0 && server.IdleFor > SessionServer.IdleTimeout)
                {
                    Log.Info("空闲超时（30 分钟无会话），自动退出");
                    cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── 命令模式 ──

    private static Task<int> RunCommandAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?" or "help")
        {
            PrintUsage();
            return Task.FromResult(0);
        }
        switch (args[0])
        {
            case "--version":
                Console.WriteLine($"噗~噗噗~~噗噗噗噗~~~~ {VersionString} (WPF)");
                return Task.FromResult(0);
            case "--register":
                RegisterShell();
                return Task.FromResult(0);
            case "--unregister":
                UnregisterShell();
                return Task.FromResult(0);
            case "--clean":
                if (IsAnotherInstanceRunning())
                {
                    Console.Error.WriteLine("噗~噗噗~~噗噗噗噗~~~~ 正在运行中——请先停止它再清理缓存（否则会删掉正在播放的产物）。");
                    return Task.FromResult(1);
                }
                CleanCache();
                return Task.FromResult(0);
            case "--install":
                InstallSelf();
                return Task.FromResult(0);
            case "--uninstall":
                UninstallSelf();
                return Task.FromResult(0);
            default:
                Console.Error.WriteLine($"未知参数: {args[0]}（pu --help 查看用法）");
                return Task.FromResult(2);
        }
    }

    private static void RegisterShell()
    {
        var exts = ShellConfig.Load();
        ShellRegister.Register(exts);
        Console.WriteLine($"已注册右键菜单：{exts.Count} 个扩展名 + 文件夹（HKCU，无需管理员）。");
        Console.WriteLine("  现在可以右键任意视频/文件夹 → 噗~噗噗~~噗噗噗噗~~~~");
        Console.WriteLine("  撤销: pu --unregister");
    }

    private static void UnregisterShell()
    {
        // 枚举 SystemFileAssociations 下全部扩展名：配置里删掉的旧扩展名也一并注销
        ShellRegister.Unregister();
        Console.WriteLine("已移除右键菜单（全部扩展名 + 文件夹）。");
    }

    private static void InstallSelf()
    {
        var destDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu");
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, "pu.exe");
        var src = Environment.ProcessPath!;
        if (!string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(src, dest, overwrite: true);
            Console.WriteLine($"已安装到 {dest}");
        }
        else
        {
            Console.WriteLine($"已是最新安装（{dest}）");
        }

        // 全自带版：ffmpeg\ 目录一并复制。运行时只找新 exe 旁的 ffmpeg，
        // 只拷 pu.exe 的话 full zip 用户安装后就找不到 ffmpeg，零依赖版反而无法播放
        var srcFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var destFfmpeg = Path.Combine(destDir, "ffmpeg");
        if (Directory.Exists(srcFfmpeg))
        {
            if (!string.Equals(Path.GetFullPath(srcFfmpeg), Path.GetFullPath(destFfmpeg),
                    StringComparison.OrdinalIgnoreCase))
            {
                CopyDirectory(srcFfmpeg, destFfmpeg);
                Console.WriteLine($"已复制 ffmpeg → {destFfmpeg}");
            }
        }
        else
        {
            Console.WriteLine("未发现自带 ffmpeg（非全自带版，继续按 PATH/配置定位）。");
        }

        var exts = ShellConfig.Load();
        ShellRegister.Register(exts, dest);
        Console.WriteLine($"已注册右键菜单：{exts.Count} 个扩展名 + 文件夹（HKCU，无需管理员）。");
        Console.WriteLine("现在可以右键任意视频/文件夹 → 噗~噗噗~~噗噗噗噗~~~~");
    }

    private static void UninstallSelf()
    {
        // 枚举全部扩展名注销：配置里已删掉的旧扩展名也一并清掉，不留残键
        ShellRegister.Unregister();
        Console.WriteLine("已移除右键菜单（全部扩展名 + 文件夹）。");

        var destDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu");
        var dest = Path.Combine(destDir, "pu.exe");
        try
        {
            if (File.Exists(dest))
            {
                if (string.Equals(Environment.ProcessPath, dest, StringComparison.OrdinalIgnoreCase))
                {
                    var bat = Path.Combine(Path.GetTempPath(), $"pu-uninstall-{Environment.ProcessId}.cmd");
                    File.WriteAllText(bat,
                        $"@echo off\r\ntimeout /t 1 /nobreak > nul\r\ndel /f /q \"{dest}\"\r\ndel /f /q \"%~f0\"\r\n");
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
                    {
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    Console.WriteLine("已安排删除 pu.exe（进程退出后生效）。");
                }
                else
                {
                    File.Delete(dest);
                    Console.WriteLine("已删除 pu.exe。");
                }
            }
        }
        catch (Exception)
        {
            Console.WriteLine($"pu.exe 被占用，请退出运行中的噗~噗噗~~噗噗噗噗~~~~后手动删除 {dest}");
        }
        try { File.Delete(Path.Combine(destDir, "extensions.json")); } catch { }
        try { Directory.Delete(Path.Combine(destDir, "ffmpeg"), recursive: true); } catch { }
    }

    /// <summary>递归复制目录（保留相对结构，覆盖已有文件）。</summary>
    private static void CopyDirectory(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(srcDir, dir)));
        foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(srcDir, file)), overwrite: true);
    }

    /// <summary>命令模式下探测服务实例是否在跑（互斥体被持有）。</summary>
    private static bool IsAnotherInstanceRunning()
    {
        try
        {
            using var mutex = new Mutex(false, MutexName);
            if (mutex.WaitOne(0))
            {
                mutex.ReleaseMutex();
                return false;
            }
            return true;
        }
        catch (AbandonedMutexException)
        {
            return false; // 前一个实例已崩溃，互斥体随之释放
        }
        catch
        {
            return false;
        }
    }

    private static void CleanCache()
    {
        var (entries, bytes) = CacheManager.Stats();
        var freed = CacheManager.Clean();
        var freedSidecar = ArtifactLocator.CleanRegistered();
        Console.WriteLine($"已清空缓存：中央缓存 {entries} 项 / {FormatSize(bytes)}"
            + $"，就地产物 {FormatSize(freedSidecar)}（共释放 {FormatSize(freed + freedSidecar)}）。");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        _ => $"{bytes} B",
    };

    private static string VersionString =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static void PrintUsage()
    {
        Console.WriteLine("""
            噗~噗噗~~噗噗噗噗~~~~ —— 右键视频，扫码即播（.NET 10 + WPF）

            用法:
              pu --install            安装到 %LOCALAPPDATA%\Pu\ 并注册右键菜单
              pu --uninstall          卸载（移除右键菜单 + 删除安装文件）
              pu --register           注册右键菜单（媒体扩展名 + 文件夹，HKCU）
              pu --unregister         移除右键菜单
              pu --clean              清空转码缓存
              pu <视频文件|文件夹>     处理并弹出 WPF 窗口（已有实例则交给它）
              pu <文件|文件夹> --debug   服务模式时输出日志到控制台（须放在路径之后）
              pu --help / --version

            右键后弹出 WPF 窗口：二维码 + 复制/打开链接 + 转码进度。
            手机/平板扫二维码即看；空闲 30 分钟自动退出；托盘可停止。
            需要 ffmpeg：https://www.gyan.dev/ffmpeg/builds/ （bin 加入 PATH 或写进 config.json）
            """);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

}
