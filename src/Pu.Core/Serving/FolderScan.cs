namespace Pu.Core.Serving;

public sealed record FolderFile(int Index, string Name, string Path, long SizeBytes);

/// <summary>文件夹模式（方案.md 第七节）：递归扫描媒体文件，非媒体自动剔除，按名排序（数字感知自然序）。</summary>
public static class FolderScan
{
    /// <summary>最大递归深度：媒体目录一般 2–4 层，12 层绰绰有余；同时防超深目录树耗尽栈。</summary>
    public const int DefaultMaxDepth = 12;

    /// <summary>遍历期安全上限：防病态巨型目录（数十万文件）耗尽内存/拖垮扫描；
    /// 命中即截断（列表不完整）。正常媒体目录远达不到。</summary>
    private const int HardCap = 100_000;

    /// <summary>扫描结果：文件列表 + 是否因达到数量上限被截断（页面据此提示“仅显示前 N 个”）。</summary>
    public sealed record ScanResult(IReadOnlyList<FolderFile> Files, bool Truncated);

    public static IReadOnlyList<FolderFile> Scan(
        string folderPath, IReadOnlyCollection<string> extensions,
        int maxFiles = 500, int maxDepth = DefaultMaxDepth)
        => ScanDetailed(folderPath, extensions, maxFiles, maxDepth).Files;

    /// <summary>扫描 + 截断标志：先扫全量（安全上限内）再自然排序、最后取前 maxFiles 个——
    /// 截断发生在排序之后，保留下的是真正的「前 N 个」，而不是枚举顺序的任意 N 个；
    /// 深度超限或安全上限同样标记截断（列表不完整）。</summary>
    public static ScanResult ScanDetailed(
        string folderPath, IReadOnlyCollection<string> extensions,
        int maxFiles = 500, int maxDepth = DefaultMaxDepth)
    {
        var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var found = new List<FolderFile>();
        var depthHit = false;
        var hardHit = false;
        ScanDir(folderPath, extSet, found, depth: 0, maxDepth, ref depthHit, ref hardHit);
        found.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        var truncated = depthHit || hardHit || found.Count > maxFiles;
        return new ScanResult(found.Take(maxFiles).Select((f, i) => f with { Index = i }).ToList(), truncated);
    }

    private static void ScanDir(
        string dir, HashSet<string> extSet, List<FolderFile> found,
        int depth, int maxDepth, ref bool depthHit, ref bool hardHit)
    {
        // 超过最大深度：深处的文件被跳过，列表不完整 → 标记截断（宁可多报）
        if (depth >= maxDepth) { depthHit = true; return; }
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
                if (found.Count >= HardCap) { hardHit = true; return; } // 病态巨型目录 → 截断
                if (extSet.Contains(Path.GetExtension(f)))
                    found.Add(new FolderFile(0, Path.GetFileName(f), f, new FileInfo(f).Length));
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        if (subdirs is null) return;
        subdirs.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var d in subdirs) ScanDir(d, extSet, found, depth + 1, maxDepth, ref depthHit, ref hardHit);
    }

    /// <summary>自然排序（数字感知）：EP2 排在 EP10 前。逐字符比较，数字段按数值比较（忽略前导零）。</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                var cx = x[i];
                var cy = y[j];
                if (char.IsDigit(cx) && char.IsDigit(cy))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var na = x.AsSpan(si, i - si).TrimStart('0');
                    var nb = y.AsSpan(sj, j - sj).TrimStart('0');
                    var cmp = na.Length != nb.Length
                        ? na.Length.CompareTo(nb.Length)
                        : na.CompareTo(nb, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                    var lenCmp = (i - si).CompareTo(j - sj); // 数值相等（01 vs 1）：较长者排后，保持稳定
                    if (lenCmp != 0) return lenCmp;
                }
                else
                {
                    var cmp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                    if (cmp != 0) return cmp;
                    i++;
                    j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
