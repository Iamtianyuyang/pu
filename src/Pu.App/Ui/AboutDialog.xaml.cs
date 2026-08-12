using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace Pu.App.Ui;

/// <summary>「关于」对话框：版本号、简介、GitHub 链接、致谢。</summary>
public sealed partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Iamtianyuyang/pupu")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开浏览器：{ex.Message}", "噗~", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
