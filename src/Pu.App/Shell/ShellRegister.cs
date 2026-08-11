using Microsoft.Win32;

namespace Pu.App.Shell;

/// <summary>
/// 右键菜单注册（方案.md 第七节，v1 注册表方式）：
///   HKCU\Software\Classes\SystemFileAssociations\.mp4\shell\Pu
///     (默认) = pu~
///     Icon   = "pu.exe",0
///     \command (默认) = "pu.exe" "%1"
/// 全部写在 HKCU，不需要管理员。
/// </summary>
public static class ShellRegister
{
    public static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("无法定位自身路径");

    public static void Register(IReadOnlyList<string> extensions)
    {
        var exe = ExePath;
        foreach (var ext in extensions)
        {
            if (!ext.StartsWith('.')) continue;
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\Pu");
            key.SetValue(null, "pu~");
            key.SetValue("Icon", $"\"{exe}\",0");
            using var command = key.CreateSubKey("command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        // 文件夹（方案.md 第七节：右键文件夹 → 列表页）
        using var dirKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\Pu");
        dirKey.SetValue(null, "pu~");
        dirKey.SetValue("Icon", $"\"{exe}\",0");
        using var dirCommand = dirKey.CreateSubKey("command");
        dirCommand.SetValue(null, $"\"{exe}\" \"%1\"");
    }

    public static void Unregister(IReadOnlyList<string> extensions)
    {
        foreach (var ext in extensions)
        {
            if (!ext.StartsWith('.')) continue;
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\Pu", throwOnMissingSubKey: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Classes\Directory\shell\Pu", throwOnMissingSubKey: false);
    }
}
