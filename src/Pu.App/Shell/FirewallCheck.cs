using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Pu.App.Ui;
using Pu.Core.Common;

namespace Pu.App.Shell;

/// <summary>
/// Windows 防火墙检查：手机扫码连不上最常见的原因是首次启动的防火墙弹窗没点「允许」
/// （或点了「取消」），而防火墙规则按 exe 路径记——重新安装到新路径后旧规则也不算数。
/// 读规则走 HNetCfg.FwPolicy2（COM，普通权限可读）；判定逻辑在 Pu.Core.Common.FirewallRules（可单测）。
/// 放行必须管理员：由用户点按钮、UAC 确认后，起一个提权的 PowerShell 加规则——程序本身始终不需要管理员。
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

    /// <summary>提权加放行规则：删掉本程序的入站拦截规则（拦截优先于放行，不删加了也没用），
    /// 再加一条所有网络类型都生效的入站 TCP 放行。弹 UAC，用户取消返回 Cancelled。</summary>
    public static async Task<(FixResult Result, string? Error)> AllowAsync(string exePath)
    {
        // 路径进单引号字符串：单引号转义成两个；整段脚本 Base64（UTF-16LE）传给 -EncodedCommand，
        // 不经过命令行引号解析——用户目录带空格、中文都不会断
        var p = exePath.Replace("'", "''");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $p = '{{p}}'
            $mine = Get-NetFirewallApplicationFilter | Where-Object {
                [Environment]::ExpandEnvironmentVariables($_.Program) -ieq $p } | Get-NetFirewallRule |
                Where-Object { $_.Direction -eq 'Inbound' }
            $mine | Where-Object { $_.Action -eq 'Block' } | Remove-NetFirewallRule
            New-NetFirewallRule -DisplayName '噗~噗噗~~噗噗噗噗~~~~' -Description '让同一个 Wi-Fi 下的手机能打开播放链接' `
                -Direction Inbound -Program $p -Protocol TCP -Action Allow -Profile Any | Out-Null
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = true, // runas 提权必须走 ShellExecute
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return (FixResult.Failed, "没能启动 PowerShell");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
            return process.ExitCode == 0
                ? (FixResult.Done, null)
                : (FixResult.Failed, $"PowerShell 退出码 {process.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED：UAC 点了「否」
        {
            return (FixResult.Cancelled, null);
        }
        catch (Exception ex)
        {
            return (FixResult.Failed, ex.Message);
        }
    }

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
