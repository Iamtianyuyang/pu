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
/// 放行必须管理员：由用户点按钮、UAC 确认后，以管理员身份再起一个本程序（--allow-firewall）改规则——主进程始终不需要管理员。
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
                    Service: (string?)r.serviceName));
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

    public enum FixResult { Done, Cancelled, Failed }

    /// <summary>提权放行：以管理员身份再启动一次本程序（pu.exe --allow-firewall），由它直接调 COM 改规则。
    /// 弹 UAC，用户取消返回 Cancelled；失败时把提权进程写回的错误原因带回来显示。
    /// 不借道 PowerShell 脚本：提权窗口看不到输出，脚本出错只剩一个退出码（实测踩过，查不出原因）。</summary>
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

    /// <summary>在提权进程里执行（pu.exe --allow-firewall &lt;错误文件&gt;）：
    /// 本程序的入站拦截规则一律禁用——拦截优先于放行，不处理加了放行也没用；
    /// 用「禁用」不用「删除」：弹窗生成的放行 / 拦截规则常常同名，按名字删可能删错。
    /// 已有「所有网络类型都生效的入站 TCP 放行」就不重复加。返回进程退出码。</summary>
    public static int AllowHere(string exePath, string? errorFile)
    {
        try
        {
            var policy = Create("HNetCfg.FwPolicy2");
            var rules = Get(policy, "Rules")!;
            var hasAllow = false;
            var disabled = 0;
            foreach (var r in (System.Collections.IEnumerable)rules)
            {
                var app = (string?)Get(r, "ApplicationName");
                if ((int)Get(r, "Direction")! != 1 || string.IsNullOrEmpty(app) || !FirewallRules.SamePath(app, exePath)) continue;
                var enabled = (bool)Get(r, "Enabled")!;
                if ((int)Get(r, "Action")! == 0)
                {
                    if (enabled) { Set(r, "Enabled", false); disabled++; }
                }
                else if (enabled && ((int)Get(r, "Profiles")! & 7) == 7 && (int)Get(r, "Protocol")! is 6 or 256)
                {
                    hasAllow = true;
                }
            }
            if (!hasAllow)
            {
                var rule = Create("HNetCfg.FWRule");
                Set(rule, "Name", "噗~噗噗~~噗噗噗噗~~~~");
                Set(rule, "Description", "让同一个 Wi-Fi 下的手机能打开播放链接");
                Set(rule, "ApplicationName", exePath);
                Set(rule, "Protocol", 6);            // TCP
                Set(rule, "Direction", 1);           // 入站
                Set(rule, "Action", 1);              // 放行
                Set(rule, "Profiles", 0x7FFFFFFF);   // 所有网络类型（Wi-Fi 可能被归为公用网络）
                Set(rule, "Enabled", true);
                rules.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, rules, [rule]);
            }
            Log.Info($"防火墙放行完成：禁用拦截规则 {disabled} 条，{(hasAllow ? "已有放行规则" : "新增放行规则")}");
            return 0;
        }
        catch (Exception caught)
        {
            // InvokeMember 把 COM 的真实错误包在 TargetInvocationException 里，剥出来才看得懂
            var ex = caught is TargetInvocationException { InnerException: { } inner } ? inner : caught;
            var message = ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80070005) }
                ? "没有管理员权限"
                : ex.Message;
            Log.Error($"防火墙放行失败: {ex}");
            if (errorFile is not null) { try { File.WriteAllText(errorFile, message); } catch { } }
            return 1;
        }
    }

    private static object Create(string progId)
        => Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
            ?? throw new InvalidOperationException($"无法创建 {progId}");

    private static void Set(object com, string name, object value)
        => com.GetType().InvokeMember(name, BindingFlags.SetProperty, null, com, [value]);

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
