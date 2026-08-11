using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Pu.Core.Serving;

public static class LanAddress
{
    /// <summary>局域网 IPv4：优先带默认网关的接口，兜底任意非回环地址。找不到返回 null。</summary>
    public static string? GetLanIpv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                return ua.Address.ToString();
            }
        }
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                return ua.Address.ToString();
            }
        }
        return null;
    }
}
