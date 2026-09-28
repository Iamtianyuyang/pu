namespace Pu.Core.Common;

/// <summary>Windows 防火墙网络类型（与 NET_FW_PROFILE_TYPE2 取值一致）。</summary>
[Flags]
public enum FwProfile
{
    None = 0,
    Domain = 1,
    Private = 2,
    Public = 4,
}

/// <summary>一条防火墙规则里判定需要的字段（Pu.App 从 HNetCfg.FwPolicy2 读出来填进来）。
/// Protocol：6 = TCP，17 = UDP，256 = 任意。LocalPorts：null/"*" = 任意端口，否则 "8000,8010-8020"。
/// Service：非空表示只作用于某个 Windows 服务，与本程序无关。
/// RemoteAddresses：null/"*" = 任意来源。Scoped：规则另有归属（UWP 应用包 / 所有者用户 SID），只对那个应用生效。</summary>
public sealed record FwRule(
    string? Application, bool Inbound, bool Allow, bool Enabled,
    FwProfile Profiles, int Protocol, string? LocalPorts = null, string? Service = null,
    string? RemoteAddresses = null, bool Scoped = false);

/// <summary>某个网络类型的防火墙总开关与默认入站策略。</summary>
public sealed record FwProfileState(FwProfile Profile, bool Enabled, bool DefaultInboundBlock, bool BlockAllInbound);

public enum FwVerdict
{
    /// <summary>当前网络下放行（或防火墙关着）。</summary>
    Allowed,
    /// <summary>没有放行本程序的规则，默认策略又是拦截——首次启动的防火墙弹窗没点「允许」。</summary>
    NotAllowed,
    /// <summary>有规则明确拦截本程序（首次弹窗点了「取消」时 Windows 会自动生成）。拦截规则优先于放行规则。</summary>
    BlockedByRule,
    /// <summary>防火墙设置了「阻止所有传入连接」，加规则也没用。</summary>
    BlockAll,
}

/// <summary>判断手机能不能连进来：只看当前生效的网络类型（Wi-Fi 被归为「公用网络」时看 Public）。
/// 纯逻辑，不碰 Windows API——读规则在 Pu.App（FirewallCheck），这里可单测。
/// 宁可漏报不误报：拿不准的规则（端口写法看不懂等）按不相关处理，最终落到默认策略。</summary>
public static class FirewallRules
{
    public static FwVerdict Evaluate(
        string exePath, int port, FwProfile active,
        IEnumerable<FwProfileState> states, IReadOnlyCollection<FwRule> rules)
    {
        var worst = FwVerdict.Allowed;
        var target = NormalizePath(exePath);
        foreach (var state in states)
        {
            if ((active & state.Profile) == 0 || !state.Enabled) continue;
            var verdict = EvaluateProfile(target, port, state, rules);
            if (verdict > worst) worst = verdict;
        }
        return worst;
    }

    private static FwVerdict EvaluateProfile(string target, int port, FwProfileState state, IReadOnlyCollection<FwRule> rules)
    {
        if (state.BlockAllInbound) return FwVerdict.BlockAll;
        var allowed = false;
        foreach (var r in rules)
        {
            if (!r.Enabled || !r.Inbound || (r.Profiles & state.Profile) == 0) continue;
            if (r.Protocol is not (6 or 256)) continue;                           // Kestrel 只走 TCP
            if (!string.IsNullOrEmpty(r.Service) && r.Service != "*") continue;   // 服务专属规则
            var anyApp = string.IsNullOrEmpty(r.Application) || r.Application == "*";
            if (anyApp)
            {
                // 不限程序的规则：端口明确写了本端口的算数；「不限程序也不限端口」的只认真正的全放行/全拦截——
                // 带归属（Store / Game Bar 等 UWP 规则靠应用包、所有者 SID 限定）或限定了来源地址
                // （加速器的 UPnP 规则只放组播地址）的都不作用于本程序。两类都实测踩过：
                // 一律当成放行会把「没有放行」误判成放行；一律忽略又漏掉真正的全放行规则（手机连上了却还报警）
                if (IsAnyPort(r.LocalPorts))
                {
                    if (r.Scoped || !IsAnyAddress(r.RemoteAddresses)) continue;
                }
                else if (!PortMatches(r.LocalPorts, port))
                {
                    continue;
                }
            }
            else
            {
                if (!string.Equals(NormalizePath(r.Application!), target, StringComparison.OrdinalIgnoreCase)) continue;
                if (!PortMatches(r.LocalPorts, port)) continue; // 限定了本程序又限定了端口：按写的端口判
            }
            if (!r.Allow) return FwVerdict.BlockedByRule;
            allowed = true;
        }
        if (allowed) return FwVerdict.Allowed;
        return state.DefaultInboundBlock ? FwVerdict.NotAllowed : FwVerdict.Allowed;
    }

    private static bool IsAnyPort(string? ports)
        => string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*";

    private static bool IsAnyAddress(string? addresses)
        => string.IsNullOrWhiteSpace(addresses) || addresses.Trim() == "*";

    /// <summary>"8000"、"8000-8031"、"80,443,8000-8031"、"*"；看不懂的写法（如 RPC、IPHTTPS 这类关键字）不匹配。</summary>
    internal static bool PortMatches(string? ports, int port)
    {
        if (IsAnyPort(ports)) return true;
        foreach (var part in ports!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, out var p) && p == port) return true;
            }
            else if (int.TryParse(part[..dash], out var lo) && int.TryParse(part[(dash + 1)..], out var hi)
                     && port >= lo && port <= hi)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>两个程序路径是否指向同一个 exe（环境变量、大小写、引号都不影响）。</summary>
    public static bool SamePath(string a, string b)
        => string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>规则里的路径可能带环境变量（%LOCALAPPDATA%\…）、大小写不一：展开 + 规范化后比较。</summary>
    private static string NormalizePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try { return Path.GetFullPath(expanded); }
        catch { return expanded; }
    }
}
