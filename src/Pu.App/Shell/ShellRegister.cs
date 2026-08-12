using Microsoft.Win32;

namespace Pu.App.Shell;

/// <summary>
/// 右键菜单注册（方案.md 第七节，v1 注册表方式）：
///   HKCU\Software\Classes\SystemFileAssociations\.mp4\shell\Pu
///     (默认) = 噗~噗噗~~噗噗噗噗~~~~
///     Icon   = "pu.exe",0
///     \command (默认) = "pu.exe" "%1"
/// 全部写在 HKCU，不需要管理员。
/// </summary>
public static class ShellRegister
{
    public static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("无法定位自身路径");

    public static void Register(IReadOnlyList<string> extensions)
        => Register(extensions, ExePath);

    public static void Register(IReadOnlyList<string> extensions, string exePath)
    {
        var exe = exePath;
        foreach (var ext in extensions)
        {
            if (!ext.StartsWith('.')) continue;
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{ext}\shell\Pu");
            key.SetValue(null, "噗~噗噗~~噗噗噗噗~~~~");
            key.SetValue("Icon", $"\"{exe}\",0");
            using var command = key.CreateSubKey("command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        // 文件夹（方案.md 第七节：右键文件夹 → 列表页）
        using var dirKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\Pu");
        dirKey.SetValue(null, "噗~噗噗~~噗噗噗噗~~~~");
        dirKey.SetValue("Icon", $"\"{exe}\",0");
        using var dirCommand = dirKey.CreateSubKey("command");
        dirCommand.SetValue(null, $"\"{exe}\" \"%1\"");
    }

    /// <summary>注销右键菜单。extensions 为可选兜底（兼容旧调用方）；
    /// 真正的清理以枚举 SystemFileAssociations 下所有 shell\Pu 为准——
    /// 从 extensions.json 删掉的旧扩展名已不在清单里，只删清单键会永久残留（含卸载后）。</summary>
    public static void Unregister(IReadOnlyList<string>? extensions = null)
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\SystemFileAssociations");
        if (root is not null)
        {
            foreach (var ext in root.GetSubKeyNames())
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\SystemFileAssociations\{ext}\shell\Pu", throwOnMissingSubKey: false);
                }
                catch { /* 单个键失败不影响其余清理 */ }
            }
        }
        if (extensions is not null)
        {
            foreach (var ext in extensions)
            {
                if (!ext.StartsWith('.')) continue;
                Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\SystemFileAssociations\{ext}\shell\Pu", throwOnMissingSubKey: false);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(
            @"Software\Classes\Directory\shell\Pu", throwOnMissingSubKey: false);
    }
}
