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

    /// <summary>由中央缓存内产物路径反推条目目录（{root}/{key}）；不在缓存内的返回 null。</summary>
    public static string? EntryDirFor(string artifactPath)
    {
        var root = RootDir;
        if (!artifactPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = artifactPath.AsSpan(root.Length).TrimStart(['\\', '/']);
        var sep = rest.IndexOfAny('\\', '/');
        var key = (sep < 0 ? rest : rest[..sep]).ToString();
        return key.Length == 0 ? null : Path.Combine(root, key);
    }

    /// <summary>命中中央缓存产物时刷新 LRU 标记（HLS 产物在 {key}/out.mp4.hls/ 子目录里，必须 touch 条目目录本身）。</summary>
    public static void TouchEntry(string artifactPath)
    {
        var dir = EntryDirFor(artifactPath) ?? Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        if (dir is not null) Touch(dir);
    }

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

    /// <summary>限频检查：每分钟最多跑一次全量统计。skipEntry 返回 true 的条目跳过淘汰（如正在播放的产物）。</summary>
    public static void MaybeEvict(Func<string, bool>? skipEntry = null)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastEvictTicks)
            < (long)MinEvictInterval.TotalMilliseconds)
            return;
        Interlocked.Exchange(ref _lastEvictTicks, Environment.TickCount64);
        Evict(skipEntry: skipEntry);
    }

    public static void Evict(long capacityBytes = DefaultCapacityBytes, Func<string, bool>? skipEntry = null)
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
            if (skipEntry is not null && skipEntry(e.Dir)) continue; // 正在被播放的条目不删
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
