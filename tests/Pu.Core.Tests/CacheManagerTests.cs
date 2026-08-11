using Pu.Core.Cache;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>缓存 LRU / 上限 / 清空测试。用 PU_CACHE_DIR 环境变量隔离，不碰真实缓存。</summary>
public class CacheManagerTests
{
    private static (string Root, IDisposable Env) IsolatedRoot()
    {
        var root = Path.Combine(TestEnv.NewTestDir(), "cache");
        var old = Environment.GetEnvironmentVariable("PU_CACHE_DIR");
        Environment.SetEnvironmentVariable("PU_CACHE_DIR", root);
        return (root, new EnvRestore(old));
    }

    private static string MakeEntry(string root, string key, long size)
    {
        var dir = Path.Combine(root, key);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "out.mp4");
        using var fs = File.Create(file);
        fs.SetLength(size); // 稀疏文件，快
        return dir;
    }

    [Fact]
    public void 超上限_按最近使用顺序淘汰最旧的()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            // 先建 old（旧），等 1.1s 再建 fresh —— mtime 精度问题用显式 Touch 控制
            var old = MakeEntry(root, "old", 100);
            CacheManager.Touch(old);
            Thread.Sleep(1100);
            var fresh = MakeEntry(root, "fresh", 100);
            CacheManager.Touch(fresh);

            // 容量 150：只装得下一个，应淘汰 old
            CacheManager.Evict(capacityBytes: 150);

            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(fresh));
        }
    }

    [Fact]
    public void 未超上限_不淘汰()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            var a = MakeEntry(root, "a", 100);
            var b = MakeEntry(root, "b", 100);
            CacheManager.Touch(a);
            CacheManager.Touch(b);

            CacheManager.Evict(capacityBytes: 500);
            Assert.True(Directory.Exists(a));
            Assert.True(Directory.Exists(b));
        }
    }

    [Fact]
    public void Touch_更新最近使用标记_被保留()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            var old = MakeEntry(root, "old", 100);
            Thread.Sleep(1100);
            var fresh = MakeEntry(root, "fresh", 100);
            CacheManager.Touch(old);   // 把更旧的 old 标记为最近使用

            CacheManager.Evict(capacityBytes: 150); // 只能留一个 → 保留 old
            Assert.True(Directory.Exists(old));
            Assert.False(Directory.Exists(fresh));
        }
    }

    [Fact]
    public void 未Touch的旧项_优先被淘汰()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            var old = MakeEntry(root, "old", 100);
            Thread.Sleep(1100);
            var fresh = MakeEntry(root, "fresh", 100);
            CacheManager.Touch(fresh); // 只有 fresh 更新过标记

            CacheManager.Evict(capacityBytes: 150);
            Assert.False(Directory.Exists(old));  // 旧项淘汰
            Assert.True(Directory.Exists(fresh)); // 命中过的保留
        }
    }

    [Fact]
    public void Evict_跳过受保护条目_只淘汰其余()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            var old = MakeEntry(root, "old", 100);
            CacheManager.Touch(old);
            Thread.Sleep(1100);
            var fresh = MakeEntry(root, "fresh", 100);
            CacheManager.Touch(fresh);

            // 容量 150 只能留一个；fresh 是较新的但被保护（如正在播放）→ 保护优先，淘汰 old
            CacheManager.Evict(capacityBytes: 150, skipEntry: dir => dir == fresh);

            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(fresh));
        }
    }

    [Fact]
    public void TouchEntry_命中HLS产物_刷新的是条目目录()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            // 模拟中央缓存 HLS 布局：{key}/out.mp4.hls/index.m3u8
            var artifact = Path.Combine(root, "key1", "out.mp4.hls", "index.m3u8");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "#EXTM3U");
            Thread.Sleep(1100);

            CacheManager.TouchEntry(artifact);
            var entry = Path.Combine(root, "key1");
            // HLS 产物在子目录里，条目目录本身的 mtime 必须被刷新（Evict 按条目目录排序）
            Assert.True(Directory.GetLastWriteTimeUtc(entry) > DateTime.UtcNow.AddSeconds(-30));
        }
    }

    [Fact]
    public void Stats_统计项数与总字节()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            MakeEntry(root, "a", 1000);
            MakeEntry(root, "b", 2000);
            var (entries, bytes) = CacheManager.Stats();
            Assert.Equal(2, entries);
            Assert.Equal(3000, bytes);
        }
    }

    [Fact]
    public void Clean_清空并返回释放字节()
    {
        var (root, env) = IsolatedRoot();
        using (env)
        {
            MakeEntry(root, "a", 1000);
            var freed = CacheManager.Clean();
            Assert.Equal(1000, freed);
            Assert.False(Directory.Exists(root));
        }
    }

    private sealed class EnvRestore(string? old) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("PU_CACHE_DIR", old);
    }
}
