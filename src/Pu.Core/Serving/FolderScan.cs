namespace Pu.Core.Serving;

public sealed record FolderFile(int Index, string Name, string Path, long SizeBytes);

/// <summary>文件夹模式（方案.md 第七节）：递归扫描媒体文件，非媒体自动剔除，按名排序。</summary>
public static class FolderScan
{
    public static IReadOnlyList<FolderFile> Scan(
        string folderPath, IReadOnlyCollection<string> extensions, int maxFiles = 500)
    {
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var found = new List<FolderFile>();
        ScanDir(folderPath, extSet, found, maxFiles);
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found.Select((f, i) => f with { Index = i }).ToList();
    }

    private static void ScanDir(string dir, HashSet<string> extSet, List<FolderFile> found, int maxFiles)
    {
        if (found.Count >= maxFiles) return;
        List<string>? subdirs = null;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir)) (subdirs ??= []).Add(d);
            foreach (var f in Directory.EnumerateFiles(dir))
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
        foreach (var d in subdirs) ScanDir(d, extSet, found, maxFiles);
    }
}
