using Pu.Core.Cache;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>产物落点：就地（.pu\）优先、清单校验复用、不可写回退中央缓存、--clean 登记清除。</summary>
public class ArtifactLocatorTests
{
    [Fact]
    public void 无产物时_复用为Null()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1, 2, 3]);
        Assert.Null(ArtifactLocator.TryGetReusable(src, "mp4", null));
    }

    [Fact]
    public void 就地生产_清单匹配后可复用()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1, 2, 3]);

        var target = ArtifactLocator.ForProduction(src, "mp4", null);
        Assert.True(target.Sidecar);
        Assert.Equal(Path.Combine(dir.Path, ".pu"), target.WorkDir);
        Assert.EndsWith("movie.mkv.mp4", target.ArtifactPath);
        Assert.True(ArtifactLocator.IsSidecarPath(target.ArtifactPath));

        // 模拟生产完成：临时文件 → 正式产物 + 清单
        File.Move(WriteAllTextRet(target.TempPath), target.ArtifactPath);
        ArtifactLocator.WriteManifest(target.ArtifactPath, src, null);

        Assert.Equal(target.ArtifactPath, ArtifactLocator.TryGetReusable(src, "mp4", null));
    }

    [Fact]
    public void 源文件变化_清单失配_不复用()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1, 2, 3]);

        var target = ArtifactLocator.ForProduction(src, "mp4", null);
        File.WriteAllText(target.ArtifactPath, "out");
        ArtifactLocator.WriteManifest(target.ArtifactPath, src, null);
        Assert.NotNull(ArtifactLocator.TryGetReusable(src, "mp4", null));

        File.AppendAllText(src, "more-bytes");
        Assert.Null(ArtifactLocator.TryGetReusable(src, "mp4", null));
    }

    [Fact]
    public void 策略变体不一致_不复用()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1]);

        var artifact = Path.Combine(dir.Path, ".pu", "movie.mkv.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "out");
        ArtifactLocator.WriteManifest(artifact, src, "gpu:h264_nvenc");

        Assert.Null(ArtifactLocator.TryGetReusable(src, "mp4", null)); // auto ≠ gpu
        Assert.Equal(artifact, ArtifactLocator.TryGetReusable(src, "mp4", "gpu:h264_nvenc"));
    }

    [Fact]
    public void 源目录不可写_回退中央缓存()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1]);

        ArtifactLocator.WritableOverride = _ => false;
        try
        {
            ArtifactLocator.ClearProbeCache();
            var target = ArtifactLocator.ForProduction(src, "mp4", "gpu:x");
            Assert.False(target.Sidecar);
            Assert.Contains(CacheManager.RootDir, target.WorkDir);
            // 回退路径与同 variant 的中央缓存一致
            Assert.Equal(CacheKey.ArtifactDirFor(src, "gpu:x"), target.WorkDir);
        }
        finally
        {
            ArtifactLocator.WritableOverride = null;
            ArtifactLocator.ClearProbeCache();
        }
    }

    [Fact]
    public void 中央缓存_先写临时名_崩溃残留不算可复用()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1]);

        ArtifactLocator.WritableOverride = _ => false;
        try
        {
            ArtifactLocator.ClearProbeCache();
            var target = ArtifactLocator.ForProduction(src, "mp4", null);
            Assert.False(target.Sidecar);
            Assert.Equal(target.ArtifactPath + ".tmp", target.TempPath);
            Assert.Null(ArtifactLocator.TryGetReusable(src, "mp4", null)); // 只有 .tmp → 不算可复用

            // 模拟生产成功：临时 → 正式，可复用
            Directory.CreateDirectory(Path.GetDirectoryName(target.TempPath)!);
            File.WriteAllText(target.TempPath, "out");
            File.Move(target.TempPath, target.ArtifactPath);
            Assert.Equal(target.ArtifactPath, ArtifactLocator.TryGetReusable(src, "mp4", null));

            // 硬崩溃残留的半截 .tmp 不能顶替正式产物
            File.WriteAllText(target.TempPath, "half-written");
            Assert.Equal(target.ArtifactPath, ArtifactLocator.TryGetReusable(src, "mp4", null));
        }
        finally
        {
            ArtifactLocator.WritableOverride = null;
            ArtifactLocator.ClearProbeCache();
        }
    }

    [Fact]
    public void 中央缓存HLS_临时目录与正式目录分离()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1]);

        ArtifactLocator.WritableOverride = _ => false;
        try
        {
            ArtifactLocator.ClearProbeCache();
            var target = ArtifactLocator.ForProduction(src, "mp4.hls", "fmt:5");
            Assert.False(target.Sidecar);
            Assert.EndsWith(Path.Combine("out.mp4.hls", "index.m3u8"), target.ArtifactPath);
            Assert.EndsWith("out.mp4.hls.tmp", target.TempPath);
            // 字幕工作目录 = 条目目录（LRU 淘汰时与产物一起走）
            Assert.Equal(CacheKey.ArtifactDirFor(src, "fmt:5"), target.WorkDir);
        }
        finally
        {
            ArtifactLocator.WritableOverride = null;
            ArtifactLocator.ClearProbeCache();
        }
    }

    [Fact]
    public void CleanRegistered_删除产物清单与空目录()
    {
        using var dir = new TempDir();
        // 登记表单独放到一个隔离的 PU_CONFIG_DIR，避免动真配置
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            var mediaDir = Path.Combine(dir.Path, "videos");
            var artifact = Path.Combine(mediaDir, ".pu", "a.mkv.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "12345");
            File.WriteAllText(artifact + ".json", "{}");

            ArtifactLocator.Register(artifact);
            var freed = ArtifactLocator.CleanRegistered();

            Assert.Equal(5, freed);
            Assert.False(File.Exists(artifact));
            Assert.False(File.Exists(artifact + ".json"));
            Assert.False(Directory.Exists(Path.GetDirectoryName(artifact))); // 空 .pu 一并删
            Assert.True(Directory.Exists(mediaDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void CleanRegistered_字幕副产物subs一并清除()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            var mediaDir = Path.Combine(dir.Path, "videos");
            // 非 HLS 产物 + 字幕副产物
            var artifact = Path.Combine(mediaDir, ".pu", "a.mkv.m4a");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "12345");
            Directory.CreateDirectory(Path.Combine(mediaDir, ".pu", "subs"));
            File.WriteAllText(Path.Combine(mediaDir, ".pu", "subs", "2.vtt"), "WEBVTT");
            ArtifactLocator.Register(artifact);

            // HLS 产物（index.m3u8 在 {name}.mp4.hls 目录里）
            var hlsArtifact = Path.Combine(mediaDir, ".pu", "b.mkv.mp4.hls", "index.m3u8");
            Directory.CreateDirectory(Path.GetDirectoryName(hlsArtifact)!);
            File.WriteAllText(hlsArtifact, "#EXTM3U");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(hlsArtifact)!, "seg_00001.ts"), "ts");
            Directory.CreateDirectory(Path.Combine(mediaDir, ".pu", "subs")); // 两个产物共用一个 subs
            ArtifactLocator.Register(hlsArtifact);

            ArtifactLocator.CleanRegistered();

            Assert.False(File.Exists(artifact));
            Assert.False(Directory.Exists(Path.Combine(mediaDir, ".pu", "b.mkv.mp4.hls")));
            Assert.False(Directory.Exists(Path.Combine(mediaDir, ".pu", "subs"))); // 字幕副产物清了
            Assert.False(Directory.Exists(Path.Combine(mediaDir, ".pu")));          // .pu 空 → 一并删
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private static string WriteAllTextRet(string path)
    {
        File.WriteAllText(path, "out");
        return path;
    }
}
