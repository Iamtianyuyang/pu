using System.Runtime.InteropServices;

namespace Pu.App.Ui;

/// <summary>应用图标：从嵌入资源提取 pu.ico 后 LoadImage 出 HICON（窗口 + 托盘共用）。</summary>
public static class AppIcons
{
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;

    public static string ExtractIcoPath()
    {
        var dest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu", "pu.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        using var src = typeof(AppIcons).Assembly.GetManifestResourceStream("pu.ico")
            ?? throw new InvalidOperationException("缺少嵌入的 pu.ico");
        using var buffer = new MemoryStream();
        src.CopyTo(buffer);
        var embedded = buffer.ToArray();

        // 只在内容变化时覆盖，避免升级后继续使用旧图标缓存。
        if (!File.Exists(dest) || !File.ReadAllBytes(dest).AsSpan().SequenceEqual(embedded))
        {
            File.WriteAllBytes(dest, embedded);
        }
        return dest;
    }

    public static IntPtr LoadHIcon(int size)
        => LoadImage(IntPtr.Zero, ExtractIcoPath(), IMAGE_ICON, size, size, LR_LOADFROMFILE);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
}
