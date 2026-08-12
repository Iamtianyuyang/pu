using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Pu.Core.Serving;

public static class LanAddress
{
    /// <summary>
    /// 局域网 IPv4：过滤掉虚拟网卡（VPN 隧道 / 虚拟机 Host-Only / Docker / 蓝牙等）后，
    /// 优先带默认网关的接口，兜底任意非回环地址；全部被过滤时退回未过滤候选（总得有个码能扫）。
    /// 不这么做的话 Tailscale/WireGuard/VirtualBox 之类的虚拟网卡常排在前面，
    /// 给出的 IP 手机根本连不上，二维码白扫。
    /// </summary>
    public static string? GetLanIpv4()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces().Select(Describe);
            return Pick(interfaces)?.ToString();
        }
        catch
        {
            // 网络栈异常（权限/平台不支持/驱动异常）：按无局域网地址处理，
            // 调用方回退 localhost（UrlFor 已有 ?? "localhost" 兜底），不拖垮服务启动
            return null;
        }
    }

    /// <summary>网卡信息的决策输入（与系统 API 解耦，测试可直接构造）。</summary>
    internal sealed record Iface(
        string Name, string Description, NetworkInterfaceType Type,
        OperationalStatus Status, IPAddress? Ipv4, bool HasGateway);

    /// <summary>测试入口：决策逻辑。顺序：可用且带网关 → 可用 → 全灭兜底（未过滤）。</summary>
    internal static IPAddress? Pick(IEnumerable<Iface> interfaces)
    {
        var list = interfaces.ToList();
        var usable = list.Where(IsUsable).ToList();

        foreach (var ni in usable.Where(i => i.HasGateway))
            if (ni.Ipv4 is { } ip) return ip;
        foreach (var ni in usable)
            if (ni.Ipv4 is { } ip) return ip;
        // 兜底：真网卡全灭（只有 VPN）时退回任意非回环，保证二维码至少可生成
        foreach (var ni in list.Where(i => IsUpNonLoopback(i.Type, i.Status)))
            if (ni.Ipv4 is { } ip) return ip;
        return null;
    }

    /// <summary>是否可作为局域网候选：在线、非回环、非虚拟网卡。</summary>
    private static bool IsUsable(Iface ni)
        => IsUpNonLoopback(ni.Type, ni.Status) && !IsVirtual(ni);

    private static bool IsUpNonLoopback(NetworkInterfaceType type, OperationalStatus status)
        => status == OperationalStatus.Up && type != NetworkInterfaceType.Loopback;

    private static bool IsVirtual(Iface ni)
    {
        if (ni.Type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return true; // Tailscale / WireGuard / OpenVPN 隧道、拨号
        var haystack = $"{ni.Name} {ni.Description}";
        // 大小写不敏感：Windows 网卡名大小写各异（VirtualBox / vEthernet / TAP- …）
        return VirtualKeywords.Any(kw => haystack.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>虚拟网卡关键词（名字或描述，如 “vEthernet (Default Switch)”“Tailscale Tunnel”）。</summary>
    private static readonly string[] VirtualKeywords =
    [
        "virtual", "vmware", "virtualbox", "vethernet", "hyper-v", "hyperv",
        "tailscale", "zerotier", "wireguard", "wintun", "tap-", "tun",
        "docker", "hamachi", "bluetooth", "loopback", "npcap", "wsl",
    ];

    private static Iface Describe(NetworkInterface ni)
    {
        IPAddress? ipv4 = null;
        var hasGateway = false;
        try
        {
            var props = ni.GetIPProperties(); // 个别接口取属性会抛异常，跳过即可
            hasGateway = props.GatewayAddresses.Count > 0;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                {
                    ipv4 = ua.Address;
                    break;
                }
            }
        }
        catch { }
        return new Iface(ni.Name, ni.Description, ni.NetworkInterfaceType, ni.OperationalStatus, ipv4, hasGateway);
    }
}
