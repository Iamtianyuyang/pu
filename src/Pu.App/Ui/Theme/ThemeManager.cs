using System.Windows;
using Microsoft.Win32;

namespace Pu.App.Ui;

/// <summary>深浅色主题：跟随 Windows「选择应用模式」（HKCU AppsUseLightTheme）。
/// 配色字典（Light/Dark.xaml）装在 Application.Resources 最前面，控件样式（Controls.xaml）在后，
/// 界面全部 DynamicResource 引用 token——换字典即全局换色，主窗口和「关于」对话框一起变。
/// 系统切换深浅色时 Windows 广播 WM_SETTINGCHANGE("ImmersiveColorSet")，主窗口转给 Refresh。</summary>
public static class ThemeManager
{
    private static readonly Uri LightUri = new("pack://application:,,,/pu;component/Ui/Theme/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/pu;component/Ui/Theme/Dark.xaml");
    private static readonly Uri ControlsUri = new("pack://application:,,,/pu;component/Ui/Theme/Controls.xaml");
    private static ResourceDictionary? s_palette;

    public static bool IsDark { get; private set; }

    /// <summary>主题变了（窗口据此换二维码四角等少量代码里画的颜色）。</summary>
    public static event Action? Changed;

    /// <summary>在 UI 线程、创建第一个窗口之前调用（重复调用安全）。</summary>
    public static void Install(Application app)
    {
        if (s_palette is not null) return;
        IsDark = ReadSystemDark();
        s_palette = new ResourceDictionary { Source = IsDark ? DarkUri : LightUri };
        app.Resources.MergedDictionaries.Insert(0, s_palette);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ControlsUri });
    }

    /// <summary>重新读系统设置，变了才换字典。</summary>
    public static void Refresh()
    {
        if (Application.Current is not { } app || s_palette is null) return;
        var dark = ReadSystemDark();
        if (dark == IsDark) return;
        IsDark = dark;
        var next = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        var list = app.Resources.MergedDictionaries;
        list[list.IndexOf(s_palette)] = next;
        s_palette = next;
        Changed?.Invoke();
    }

    /// <summary>PU_THEME=dark|light 可强制指定（截图 / 排查用）；否则读系统「应用模式」，读不到按浅色。</summary>
    private static bool ReadSystemDark()
    {
        var forced = Environment.GetEnvironmentVariable("PU_THEME");
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
