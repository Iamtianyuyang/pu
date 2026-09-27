using System.Windows;
using System.Windows.Controls;
using Pu.Core.Common;

namespace Pu.App.Ui;

/// <summary>防火墙提示卡片：按判定结果说明原因，给「放行」（提权加规则）和「自己去设置」两个出口。
/// 只负责显示；显不显示由 MainWindow 决定（已送达 / 转码失败时不显示）。</summary>
public sealed partial class FirewallHint : UserControl
{
    public event Action? FixRequested;
    public event Action? SettingsRequested;

    public FirewallHint()
    {
        InitializeComponent();
    }

    /// <summary>按判定结果填文案；Allowed / null 不该调用（调用方直接隐藏卡片）。</summary>
    public void Render(FwVerdict verdict, string? note, bool busy)
    {
        BodyText.Text = verdict switch
        {
            FwVerdict.BlockedByRule => "Windows 防火墙里有一条规则拦住了噗噗（多半是第一次启动时，防火墙弹窗点了「取消」）。",
            FwVerdict.BlockAll => "Windows 防火墙开着「阻止所有传入连接」，要在防火墙设置里把它关掉，手机才能连上。",
            _ => "Windows 防火墙还没有放行噗噗，同一个 Wi-Fi 下的手机扫码也打不开。",
        };
        // 「阻止所有传入连接」加规则也没用：只留去设置的入口
        FixButton.Visibility = verdict == FwVerdict.BlockAll ? Visibility.Collapsed : Visibility.Visible;
        FixButton.IsEnabled = !busy;
        SettingsButton.Content = verdict == FwVerdict.BlockAll ? "打开防火墙设置" : "自己去设置";
        NoteText.Text = note ?? "";
        NoteText.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FixButton_Click(object sender, RoutedEventArgs e) => FixRequested?.Invoke();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
}
