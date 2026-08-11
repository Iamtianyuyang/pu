namespace Pu.Core.Cache;

/// <summary>
/// 缓存管理（方案.md 第八节）：默认上限 20 GB，LRU 淘汰，pu --clean 手动清空。
/// 缓存项 = {sha1 键} 目录；LastWriteTimeUtc 作为最近使用标记（命中时 Touch）。
/// 正在被浏览器读取的文件删除会失败，跳过即可。
/// </summary>
public static class CacheManager
{
    public const long DefaultCapacityBytes = 20L * 1024 * 1024 * 1024; // 20 GB

    private static readonly TimeSpan MinEvictInterval = TimeSpan.FromMinutes(1);
    private static long _lastEvictTicks;

    public static string RootDir => CachePaths.RootDir();

    /// <summary>命中缓存时更新 LRU 标记。</summary>
    public static void Touch(string entryDir)
    {
        try { Directory.SetLastWriteTimeUtc(entryDir, DateTime.UtcNow); } catch { /* 尽力而为 */ }
    }

    public static (int Entries, long TotalBytes) Stats()
    {
        if (!Directory.Exists(RootDir)) return (0, 0);
        int entries = 0;
        long total = 0;
        foreach (var dir in Directory.EnumerateDirectories(RootDir))
        {
            entries++;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    total += new FileInfo(f).Length;
            }
            catch { }
        }
        return (entries, total);
    }

    /// <summary>限频检查：每分钟最多跑一次全量统计。</summary>
    public static void MaybeEvict()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastEvictTicks)
            < (long)MinEvictInterval.TotalMilliseconds)
            return;
        Interlocked.Exchange(ref _lastEvictTicks, Environment.TickCount64);
        Evict();
    }

    public static void Evict(long capacityBytes = DefaultCapacityBytes)
    {
        if (!Directory.Exists(RootDir)) return;
        var entries = new List<(string Dir, long Size, DateTime LastUse)>();
        foreach (var dir in Directory.EnumerateDirectories(RootDir))
        {
            long size = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    size += new FileInfo(f).Length;
            }
            catch { }
            entries.Add((dir, size, Directory.GetLastWriteTimeUtc(dir)));
        }
        long total = entries.Sum(e => e.Size);
        if (total <= capacityBytes) return;
        foreach (var e in entries.OrderBy(e => e.LastUse))
        {
            if (total <= capacityBytes) break;
            try { Directory.Delete(e.Dir, recursive: true); total -= e.Size; } catch { /* 被占用跳过 */ }
        }
    }

    /// <summary>清空缓存，返回释放的字节数。</summary>
    public static long Clean()
    {
        if (!Directory.Exists(RootDir)) return 0;
        long freed = 0;
        foreach (var f in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories))
        {
            try { freed += new FileInfo(f).Length; } catch { }
        }
        try { Directory.Delete(RootDir, recursive: true); } catch { }
        return freed;
    }
}
