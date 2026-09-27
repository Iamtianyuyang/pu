namespace Pu.Core.Serving;

/// <summary>扫码送达记录：哪些局域网设备打开过这个链接（按 IP 去重）。
/// 电脑窗口据此从「等扫码」切到「送到了」，并显示设备名。
/// 只记设备类别与 IP，不落盘；上限 8 台，防止被同网段扫描刷爆。</summary>
public sealed class ClientLog
{
    private const int Max = 8;
    private readonly object _gate = new();
    private readonly List<(string Ip, string Device)> _items = [];

    /// <summary>新设备（新 IP）返回 true；已记录过或已满返回 false。</summary>
    public bool TryAdd(string ip, string device)
    {
        lock (_gate)
        {
            if (_items.Count >= Max || _items.Exists(i => i.Ip == ip)) return false;
            _items.Add((ip, device));
            return true;
        }
    }

    /// <summary>来过的设备名（按到达顺序，同类设备去重：两台 iPhone 只显示一个「iPhone」）。</summary>
    public IReadOnlyList<string> Devices
    {
        get { lock (_gate) return _items.Select(i => i.Device).Distinct().ToList(); }
    }

    public bool Any { get { lock (_gate) return _items.Count > 0; } }
}

/// <summary>设备名识别。优先用页面 JS 报上来的提示（d 参数）：iPadOS 13+ 的 Safari
/// 默认伪装成 Mac（UA 里只有 Macintosh），服务端单看 UA 分不出 iPad 和 Mac，
/// 页面能用 maxTouchPoints 区分。提示只认白名单取值，其余回退 UA 粗判。</summary>
public static class ClientDevice
{
    private static readonly Dictionary<string, string> Hints = new(StringComparer.Ordinal)
    {
        ["ipad"] = "iPad",
        ["iphone"] = "iPhone",
        ["android-phone"] = "安卓手机",
        ["android-tablet"] = "安卓平板",
        ["mac"] = "Mac",
        ["windows"] = "Windows 电脑",
        ["other"] = "浏览器",
    };

    public static string Label(string? hint, string? userAgent)
    {
        if (hint is not null && Hints.TryGetValue(hint, out var label)) return label;
        var ua = userAgent ?? "";
        if (ua.Contains("iPad", StringComparison.Ordinal)) return "iPad";
        if (ua.Contains("iPhone", StringComparison.Ordinal)) return "iPhone";
        if (ua.Contains("Android", StringComparison.Ordinal))
            return ua.Contains("Mobile", StringComparison.Ordinal) ? "安卓手机" : "安卓平板";
        if (ua.Contains("Macintosh", StringComparison.Ordinal)) return "Mac";
        if (ua.Contains("Windows", StringComparison.Ordinal)) return "Windows 电脑";
        return "浏览器";
    }
}

/// <summary>一次送达：Token 可能是播放页（/s/）也可能是文件夹页（/f/）的 token。</summary>
public sealed record ClientArrival(string Token, string Device);
