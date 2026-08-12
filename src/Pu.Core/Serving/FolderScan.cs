namespace Pu.Core.Serving;

public sealed record FolderFile(int Index, string Name, string Path, long SizeBytes);

/// <summary>文件夹模式（方案.md 第七节）：递归扫描媒体文件，非媒体自动剔除，按名排序。</summary>
public static class FolderScan
{
    /// <summary>最大递归深度：媒体目录一般 2–4 层，12 层绰绰有余；同时防超深目录树耗尽栈。</summary>
    public const int DefaultMaxDepth = 12;

    public static IReadOnlyList<FolderFile> Scan(
        string folderPath, IReadOnlyCollection<string> extensions,
        int maxFiles = 500, int maxDepth = DefaultMaxDepth)
    {
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var found = new List<FolderFile>();
        ScanDir(folderPath, extSet, found, maxFiles, depth: 0, maxDepth);
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found.Select((f, i) => f with { Index = i }).ToList();
    }

    private static void ScanDir(
        string dir, HashSet<string> extSet, List<FolderFile> found, int maxFiles, int depth, int maxDepth)
    {
        if (found.Count >= maxFiles || depth >= maxDepth) return;
        List<string>? subdirs = null;
        try
        {
            // 跳过隐藏/系统目录（沿用默认行为）＋ reparse point（junction/symlink 可指向父目录形成循环；
            // OneDrive/坚果云“仅联机”占位文件也是 reparse point——不跳过会被扫进列表，
            // 点开时 ffprobe 触发按需下载，卡住或失败）
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            foreach (var d in Directory.EnumerateDirectories(dir, "*", options)) (subdirs ??= []).Add(d);
            foreach (var f in Directory.EnumerateFiles(dir, "*", options))
            {
                if (found.Count >= maxFiles) return;
                if (extSet.Contains(Path.GetExtension(f)))
                    found.Add(new FolderFile(0, Path.GetFileName(f), f, new FileInfo(f).Length));
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        if (subdirs is null) return;
        subdirs.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var d in subdirs) ScanDir(d, extSet, found, maxFiles, depth + 1, maxDepth);
    }
}
