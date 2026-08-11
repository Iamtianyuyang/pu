using System.Net;
using System.Net.NetworkInformation;
using Pu.Core.Serving;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>LanAddress 决策逻辑：虚拟网卡过滤 + 网关优先 + 全灭兜底（注入假网卡，不依赖真机环境）。</summary>
public class LanAddressTests
{
    private static LanAddress.Iface If(
        string name, string description, NetworkInterfaceType type, OperationalStatus status,
        string? ip, string? gateway)
        => new(name, description, type, status, ip is null ? null : IPAddress.Parse(ip),
            gateway is not null);

    [Fact]
    public void VPN隧道排前面_物理网卡胜出()
    {
        var vpn = If("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Tunnel,
            OperationalStatus.Up, "100.64.0.2", "100.64.0.1");
        var eth = If("以太网", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, "192.168.1.5", "192.168.1.1");

        Assert.Equal("192.168.1.5", LanAddress.Pick([vpn, eth])?.ToString());
    }

    [Fact]
    public void 虚拟机HostOnly网卡_被跳过()
    {
        var vbox = If("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter",
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, "192.168.56.1", null);
        var wifi = If("WLAN", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up, "192.168.1.9", "192.168.1.1");

        Assert.Equal("192.168.1.9", LanAddress.Pick([vbox, wifi])?.ToString());
    }

    [Fact]
    public void 无网关的物理网卡_作为兜底被选()
    {
        var noGw = If("以太网", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, "169.254.10.5", null);

        Assert.Equal("169.254.10.5", LanAddress.Pick([noGw])?.ToString());
    }

    [Fact]
    public void 全部是虚拟网卡_退回未过滤候选_保证有码可扫()
    {
        var vpn = If("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Tunnel,
            OperationalStatus.Up, "100.64.0.2", "100.64.0.1");

        Assert.Equal("100.64.0.2", LanAddress.Pick([vpn])?.ToString());
    }

    [Fact]
    public void 全部断开_返回null()
    {
        var down = If("以太网", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet,
            OperationalStatus.Down, "192.168.1.5", "192.168.1.1");

        Assert.Null(LanAddress.Pick([down]));
    }

    [Fact]
    public void 回环地址_不会被选()
    {
        var loopback = If("Loopback", "Software Loopback Interface 1", NetworkInterfaceType.Loopback,
            OperationalStatus.Up, "127.0.0.1", "127.0.0.1");

        Assert.Null(LanAddress.Pick([loopback]));
    }
}
