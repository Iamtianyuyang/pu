using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Pu.App.Ui;
using Pu.Core.Common;

namespace Pu.App.Shell;

/// <summary>
/// Windows 防火墙检查：手机扫码连不上最常见的原因是首次启动的防火墙弹窗没点「允许」
/// （或点了「取消」），而防火墙规则按 exe 路径记——重新安装到新路径后旧规则也不算数。
/// 读规则走 HNetCfg.FwPolicy2（COM，普通权限可读）；判定逻辑在 Pu.Core.Common.FirewallRules（可单测）。
/// 放行必须管理员：由用户点按钮、UAC 确认后，以管理员身份再起一个本程序（--allow-firewall）调 netsh 改规则——主进程始终不需要管理员。
/// </summary>
public static class FirewallCheck
{
    /// <summary>检查当前网络下本程序的入站 TCP 是否放行。读不到（COM 不可用、策略受限等）返回 null——不提示，宁可漏报。</summary>
    public static FwVerdict? Check(string exePath, int port)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false);
            if (type is null) return null;
            var policy = Activator.CreateInstance(type);
            if (policy is null) return null;
            var active = (FwProfile)(int)Get(policy, "CurrentProfileTypes")!;
            var states = new List<FwProfileState>();
            foreach (var p in new[] { FwProfile.Domain, FwProfile.Private, FwProfile.Public })
            {
                states.Add(new FwProfileState(p,
                    Enabled: (bool)Get(policy, "FirewallEnabled", (int)p)!,
                    DefaultInboundBlock: (int)Get(policy, "DefaultInboundAction", (int)p)! == 0, // NET_FW_ACTION_BLOCK
                    BlockAllInbound: (bool)Get(policy, "BlockAllInboundTraffic", (int)p)!));
            }

            var rules = new List<FwRule>();
            foreach (dynamic r in (dynamic)Get(policy, "Rules")!)
            {
                rules.Add(new FwRule(
                    Application: (string?)r.ApplicationName,
                    Inbound: (int)r.Direction == 1,   // NET_FW_RULE_DIR_IN
                    Allow: (int)r.Action == 1,        // NET_FW_ACTION_ALLOW
                    Enabled: (bool)r.Enabled,
                    Profiles: (FwProfile)(int)r.Profiles,
                    Protocol: (int)r.Protocol,
                    LocalPorts: (string?)r.LocalPorts,
                    Service: (string?)r.serviceName,
                    RemoteAddresses: (string?)r.RemoteAddresses,
                    // INetFwRule3：UWP / 按用户的规则带应用包或所有者 SID，只作用于那个应用；老系统没这两个属性按无归属算。
                    // 只有「不限程序也不限端口」的规则判定时才看归属，其余不读——几百条规则逐条多读两个属性要慢 10 倍
                    Scoped: IsWildcard((string?)r.ApplicationName) && IsWildcard((string?)r.LocalPorts)
                            && (!string.IsNullOrEmpty(TryGetString(r, "LocalUserOwner"))
                                || !string.IsNullOrEmpty(TryGetString(r, "LocalAppPackageId")))));
            }
            return FirewallRules.Evaluate(exePath, port, active, states, rules);
        }
        catch (Exception ex)
        {
            Log.Info($"防火墙检查失败（不提示）: {ex.Message}");
            return null;
        }
    }

    private static object? Get(object com, string name, params object[] args)
        => com.GetType().InvokeMember(name, BindingFlags.GetProperty, null, com, args);

    private static bool IsWildcard(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "*";

    private static string? TryGetString(object com, string name)
    {
        try { return Get(com, name) as string; }
        catch { return null; }
    }

    public enum FixResult { Done, Cancelled, Failed }

    /// <summary>提权放行：以管理员身份再启动一次本程序（pu.exe --allow-firewall），由它直接调 COM 改规则。
    /// 弹 UAC，用户取消返回 Cancelled；失败时把提权进程写回的错误原因带回来显示。
    /// 提权子进程自己收集错误原因再写回：提权窗口是隐藏的，只拿退出码的话出错了无从排查（实测踩过）。</summary>
    public static async Task<(FixResult Result, string? Error)> AllowAsync(string exePath)
    {
        var errorFile = Path.Combine(Path.GetTempPath(), $"pu-firewall-{Guid.NewGuid():N}.txt");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, $"--allow-firewall \"{errorFile}\"")
            {
                UseShellExecute = true, // runas 提权必须走 ShellExecute
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return (FixResult.Failed, "没能启动提权进程");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
            if (process.ExitCode == 0) return (FixResult.Done, null);
            var reason = File.Exists(errorFile) ? File.ReadAllText(errorFile).Trim() : "";
            return (FixResult.Failed, reason.Length > 0 ? reason : $"退出码 {process.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED：UAC 点了「否」
        {
            return (FixResult.Cancelled, null);
        }
        catch (Exception ex)
        {
            return (FixResult.Failed, ex.Message);
        }
        finally
        {
            try { File.Delete(errorFile); } catch { }
        }
    }

    /// <summary>在提权进程里执行（pu.exe --allow-firewall &lt;错误文件&gt;）：用系统自带的 netsh 改规则。
    /// 先删本程序的全部入站规则（含弹窗点「取消」生成的拦截规则——拦截优先于放行，不删加了也没用），
    /// 再加一条所有网络类型、入站 TCP 放行。返回进程退出码，失败时把 netsh 的输出写进错误文件。
    /// 不用 COM 的 INetFwRules.Add：实机提权后报 E_INVALIDARG，提权环境里无从排查；netsh 是系统自带的
    /// 标准做法，失败会给出人话说明。按程序路径删（不按名字删）：弹窗生成的规则常同名，按名删可能删到别的程序。</summary>
    public static int AllowHere(string exePath, string? errorFile)
    {
        try
        {
            // 路径里不可能出现双引号（Windows 文件名禁止），直接加引号拼接是安全的；不经过 cmd，没有转义问题
            RunNetsh($"advfirewall firewall delete rule name=all dir=in program=\"{exePath}\""); // 没有旧规则时 netsh 返回 1，忽略
            var (code, output) = RunNetsh(
                $"advfirewall firewall add rule name=\"噗~噗噗~~噗噗噗噗~~~~\" dir=in action=allow " +
                $"program=\"{exePath}\" protocol=TCP profile=any enable=yes " +
                "description=\"让同一个 Wi-Fi 下的手机能打开播放链接\"");
            if (code != 0) throw new InvalidOperationException(output.Length > 0 ? output : $"netsh 退出码 {code}");
            Log.Info("防火墙放行完成（netsh）");
            return 0;
        }
        catch (Exception ex)
        {
            var message = ex is Win32Exception { NativeErrorCode: 5 } ? "没有管理员权限" : ex.Message;
            Log.Error($"防火墙放行失败: {ex}");
            if (errorFile is not null) { try { File.WriteAllText(errorFile, message); } catch { } }
            return 1;
        }
    }

    /// <summary>跑 netsh 并收集输出。netsh 输出的编码没法事先确定：同一台中文系统上 GetOEMCP 报 936，
    /// 重定向时 netsh 却写 UTF-8（实测，按 936 解出「璇锋眰…」乱码）。所以按原始字节读，
    /// 先严格按 UTF-8 解，解不通（真是 GBK 等）再退回系统 OEM 代码页。</summary>
    private static (int Code, string Output) RunNetsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("没能启动 netsh");
        var stdout = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        var stderr = ReadAllBytesAsync(process.StandardError.BaseStream);
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("netsh 30 秒没有响应");
        }
        return (process.ExitCode, (Decode(stdout.Result) + Decode(stderr.Result)).Trim());
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        try
        {
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding((int)GetOEMCP()).GetString(bytes);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>打开「允许应用通过 Windows 防火墙」设置页（用户想自己动手时）。</summary>
    public static void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Info($"打开防火墙设置失败: {ex.Message}");
        }
    }
}
