using Pu.Core.Common;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>防火墙判定：手机连不上的几种典型原因能认出来，放行 / 关防火墙时不误报。</summary>
public class FirewallRulesTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\pu~\pu.exe";
    private const int Port = 8000;

    private static readonly FwProfileState[] PublicOn =
    [
        new(FwProfile.Domain, true, true, false),
        new(FwProfile.Private, false, true, false),
        new(FwProfile.Public, true, true, false),
    ];

    private static FwVerdict Eval(params FwRule[] rules) => Eval(FwProfile.Public, PublicOn, rules);

    private static FwVerdict Eval(FwProfile active, FwProfileState[] states, params FwRule[] rules)
        => FirewallRules.Evaluate(Exe, Port, active, states, rules);

    private static FwRule Rule(string? app, bool allow, FwProfile profiles = FwProfile.Public,
        int protocol = 6, bool inbound = true, bool enabled = true, string? ports = null, string? service = null,
        string? remote = null, bool scoped = false)
        => new(app, inbound, allow, enabled, profiles, protocol, ports, service, remote, scoped);

    [Fact]
    public void 公用网络_没有任何规则_默认拦截_判为未放行()
        => Assert.Equal(FwVerdict.NotAllowed, Eval());

    [Fact]
    public void 放行规则_只放行了旧安装路径_仍判为未放行()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(@"C:\Users\me\AppData\Local\Pu\pu.exe", allow: true)));

    [Fact]
    public void 放行规则_路径大小写不同_仍算放行()
        => Assert.Equal(FwVerdict.Allowed, Eval(Rule(Exe.ToLowerInvariant(), allow: true)));

    [Fact]
    public void 放行规则_只对专用网络生效_当前是公用网络_判为未放行()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(Exe, allow: true, profiles: FwProfile.Private)));

    [Fact]
    public void 放行规则_所有网络类型_算放行()
        => Assert.Equal(FwVerdict.Allowed, Eval(Rule(Exe, allow: true, profiles: (FwProfile)0x7FFFFFFF)));

    [Fact]
    public void 只放行了UDP_判为未放行()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(Exe, allow: true, protocol: 17)));

    [Fact]
    public void 放行规则被禁用_判为未放行()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(Exe, allow: true, enabled: false)));

    [Fact]
    public void 拦截规则优先于放行规则()
        => Assert.Equal(FwVerdict.BlockedByRule, Eval(Rule(Exe, allow: true), Rule(Exe, allow: false)));

    [Fact]
    public void 出站规则不影响判定()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(Exe, allow: true, inbound: false)));

    [Fact]
    public void 当前网络的防火墙关着_不报警()
    {
        FwProfileState[] off = [new(FwProfile.Private, false, true, false), new(FwProfile.Public, true, true, false)];
        Assert.Equal(FwVerdict.Allowed, Eval(FwProfile.Private, off));
    }

    [Fact]
    public void 默认策略是放行_没有规则也不报警()
        => Assert.Equal(FwVerdict.Allowed, Eval(FwProfile.Public, [new(FwProfile.Public, true, false, false)]));

    [Fact]
    public void 阻止所有传入连接_单独识别()
        => Assert.Equal(FwVerdict.BlockAll, Eval(FwProfile.Public, [new(FwProfile.Public, true, true, true)], Rule(Exe, allow: true)));

    [Fact]
    public void 不限程序但放行了本端口_算放行()
        => Assert.Equal(FwVerdict.Allowed, Eval(Rule(null, allow: true, ports: "80,8000-8031")));

    [Fact]
    public void 不限程序的其它端口规则_不算数()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(null, allow: true, ports: "445")));

    [Fact]
    public void 服务专属规则_不算数()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(Exe, allow: true, service: "Spooler")));

    [Fact]
    public void 带归属的通配拦截规则_不据此报警()
        => Assert.Equal(FwVerdict.Allowed, Eval(Rule(Exe, allow: true), Rule(null, allow: false, protocol: 256, scoped: true)));

    [Fact]
    public void 不带任何限定的全拦截规则_判为被拦()
        => Assert.Equal(FwVerdict.BlockedByRule, Eval(Rule(Exe, allow: true), Rule(null, allow: false, protocol: 256)));

    [Fact]
    public void 带归属或限定来源的通配放行规则_不算放行()
        // Store / Game Bar 等 UWP 规则：程序和端口都空，靠应用包 / 所有者 SID 限定；
        // 加速器的 UPnP 规则只放组播地址。实测这类规则曾把「没有任何放行」误判成放行
        => Assert.Equal(FwVerdict.NotAllowed, Eval(
            Rule(null, allow: true, protocol: 256, scoped: true),
            Rule("*", allow: true, protocol: 256, scoped: true),
            Rule(null, allow: true, protocol: 256, remote: "239.255.255.254/255.255.255.255")));

    [Fact]
    public void 不带任何限定的全放行规则_算放行()
        // 实机：一条「WindowsPerf-In」规则不限程序/端口/协议/来源、无归属——手机确实连得上，不该再报警
        => Assert.Equal(FwVerdict.Allowed, Eval(Rule(null, allow: true, protocol: 256, remote: "*")));

    [Fact]
    public void 限定了本程序但端口不对_不算放行()
        => Assert.Equal(FwVerdict.NotAllowed, Eval(Rule(Exe, allow: true, ports: "9000")));

    [Fact]
    public void 多个生效网络_取最坏的()
    {
        FwProfileState[] both = [new(FwProfile.Private, true, true, false), new(FwProfile.Public, true, true, false)];
        Assert.Equal(FwVerdict.NotAllowed, Eval(FwProfile.Private | FwProfile.Public, both, Rule(Exe, allow: true, profiles: FwProfile.Private)));
    }

    [Theory]
    [InlineData("8000", true)]
    [InlineData("7999-8001", true)]
    [InlineData("80, 443, 8000", true)]
    [InlineData("*", true)]
    [InlineData(null, true)]
    [InlineData("8001-8031", false)]
    [InlineData("RPC", false)]
    public void 端口写法(string? ports, bool match)
        => Assert.Equal(match, FirewallRules.PortMatches(ports, Port));
}
