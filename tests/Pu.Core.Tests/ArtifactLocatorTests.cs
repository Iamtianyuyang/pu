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
        // 产物名内嵌源指纹键：源替换/策略变更 → 新键 → 新路径，互不覆盖
        Assert.EndsWith($"movie.mkv.{CacheKey.For(src)}.mp4", target.ArtifactPath);
        Assert.True(ArtifactLocator.IsSidecarPath(target.ArtifactPath));
        Assert.Equal(Path.Combine(dir.Path, ".pu"), ArtifactLocator.SidecarDirOf(target.ArtifactPath));

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

        var artifact = Path.Combine(dir.Path, ".pu", $"movie.mkv.{CacheKey.For(src, "gpu:h264_nvenc")}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "out");
        ArtifactLocator.WriteManifest(artifact, src, "gpu:h264_nvenc");

        Assert.Null(ArtifactLocator.TryGetReusable(src, "mp4", null)); // auto ≠ gpu
        Assert.Equal(artifact, ArtifactLocator.TryGetReusable(src, "mp4", "gpu:h264_nvenc"));
    }

    [Fact]
    public void 源替换或策略变更_就地产物路径互不覆盖()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(src, [1, 2, 3]);

        var a = ArtifactLocator.ForProduction(src, "mp4.hls", "fmt:5");
        var b = ArtifactLocator.ForProduction(src, "mp4.hls", "gpu:h264_nvenc;fmt:5");
        // 变体不同 → 产物目录不同，互不覆盖（旧播放会话的产物保留）
        Assert.NotEqual(a.ArtifactPath, b.ArtifactPath);

        File.AppendAllText(src, "more"); // 源被替换 → 指纹变
        var c = ArtifactLocator.ForProduction(src, "mp4.hls", "fmt:5");
        Assert.NotEqual(a.ArtifactPath, c.ArtifactPath);

        // 同指纹同变体 → 路径稳定（可复用命中）
        var d = ArtifactLocator.ForProduction(src, "mp4.hls", "fmt:5");
        Assert.Equal(c.ArtifactPath, d.ArtifactPath);
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
            Directory.CreateDirectory(mediaDir);
            var src = Path.Combine(mediaDir, "a.mkv");
            File.WriteAllBytes(src, [1, 2, 3]);
            var artifact = Path.Combine(mediaDir, ".pu", $"a.mkv.{CacheKey.For(src)}.mp4");
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
    public void CleanRegistered_字幕副产物按源指纹隔离并清除()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            var mediaDir = Path.Combine(dir.Path, "videos");
            Directory.CreateDirectory(mediaDir);
            // 非 HLS 产物 + 按源指纹隔离的字幕目录（.pu/{指纹}/subs）
            var srcA = Path.Combine(mediaDir, "a.mkv");
            File.WriteAllBytes(srcA, [1, 2, 3]);
            var artifact = Path.Combine(mediaDir, ".pu", $"a.mkv.{CacheKey.For(srcA)}.m4a");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "12345");
            ArtifactLocator.WriteManifest(artifact, srcA, null);
            var subsA = Path.Combine(mediaDir, ".pu", CacheKey.For(srcA), "subs");
            Directory.CreateDirectory(subsA);
            File.WriteAllText(Path.Combine(subsA, "2.vtt"), "WEBVTT");
            ArtifactLocator.Register(artifact);

            // HLS 产物（index.m3u8 在 {name}.mp4.hls 目录里）+ 自己的字幕目录
            var srcB = Path.Combine(mediaDir, "b.mkv");
            File.WriteAllBytes(srcB, [4, 5, 6]);
            var hlsArtifact = Path.Combine(mediaDir, ".pu", $"b.mkv.{CacheKey.For(srcB)}.mp4.hls", "index.m3u8");
            Directory.CreateDirectory(Path.GetDirectoryName(hlsArtifact)!);
            File.WriteAllText(hlsArtifact, "#EXTM3U");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(hlsArtifact)!, "seg_00001.ts"), "ts");
            ArtifactLocator.WriteManifest(hlsArtifact, srcB, null);
            var subsB = Path.Combine(mediaDir, ".pu", CacheKey.For(srcB), "subs");
            Directory.CreateDirectory(subsB);
            File.WriteAllText(Path.Combine(subsB, "1.vtt"), "WEBVTT");
            ArtifactLocator.Register(hlsArtifact);

            ArtifactLocator.CleanRegistered();

            Assert.False(File.Exists(artifact));
            Assert.False(Directory.Exists(Path.Combine(mediaDir, ".pu", "b.mkv.mp4.hls")));
            Assert.False(Directory.Exists(subsA));  // 各产物的字幕目录按自己的指纹删除
            Assert.False(Directory.Exists(subsB));
            Assert.False(Directory.Exists(Path.Combine(mediaDir, ".pu"))); // .pu 空 → 一并删
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void CleanRegistered_旧版无键清单_整删subs兜底()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            var mediaDir = Path.Combine(dir.Path, "videos");
            Directory.CreateDirectory(mediaDir);
            var src = Path.Combine(mediaDir, "a.mkv");
            File.WriteAllBytes(src, [1, 2, 3]);
            var artifact = Path.Combine(mediaDir, ".pu", $"a.mkv.{CacheKey.For(src)}.m4a");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, "12345");
            File.WriteAllText(artifact + ".json", "{}"); // 旧版清单：没有 SubsKey
            Directory.CreateDirectory(Path.Combine(mediaDir, ".pu", "subs"));
            File.WriteAllText(Path.Combine(mediaDir, ".pu", "subs", "2.vtt"), "WEBVTT");
            ArtifactLocator.Register(artifact);

            ArtifactLocator.CleanRegistered();

            Assert.False(Directory.Exists(Path.Combine(mediaDir, ".pu", "subs")));
            Assert.False(Directory.Exists(Path.Combine(mediaDir, ".pu")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void CleanRegistered_源文件已删除或修改_仍按清单键删字幕()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            var mediaDir = Path.Combine(dir.Path, "videos");
            Directory.CreateDirectory(mediaDir);
            var src = Path.Combine(mediaDir, "a.mkv");
            File.WriteAllBytes(src, [1, 2, 3]);

            var target = ArtifactLocator.ForProduction(src, "m4a", null);
            File.WriteAllText(target.TempPath, "out");
            File.Move(target.TempPath, target.ArtifactPath); // 模拟生产完成
            ArtifactLocator.WriteManifest(target.ArtifactPath, src, null);
            var subsDir = Path.Combine(Path.GetDirectoryName(target.ArtifactPath)!,
                CacheKey.For(src), "subs");
            Directory.CreateDirectory(subsDir);
            File.WriteAllText(Path.Combine(subsDir, "2.vtt"), "WEBVTT");
            ArtifactLocator.Register(target.ArtifactPath);

            // 源文件被删除（再算指纹会抛异常 / 换指纹）——clean 必须仍按清单里的键删除字幕
            File.Delete(src);
            ArtifactLocator.CleanRegistered();
            Assert.False(Directory.Exists(subsDir));

            // 源文件被修改（指纹变化）——同样按生产时的键删除
            var src2 = Path.Combine(mediaDir, "b.mkv");
            File.WriteAllBytes(src2, [7, 8, 9]);
            ArtifactLocator.ClearProbeCache(); // 上一步 clean 删掉了 .pu，重新探测会再创建
            var t2 = ArtifactLocator.ForProduction(src2, "m4a", null);
            File.WriteAllText(t2.TempPath, "out");
            File.Move(t2.TempPath, t2.ArtifactPath);
            ArtifactLocator.WriteManifest(t2.ArtifactPath, src2, null);
            var subsB = Path.Combine(Path.GetDirectoryName(t2.ArtifactPath)!,
                CacheKey.For(src2), "subs");
            Directory.CreateDirectory(subsB);
            File.WriteAllText(Path.Combine(subsB, "2.vtt"), "WEBVTT");
            ArtifactLocator.Register(t2.ArtifactPath);
            File.AppendAllText(src2, "changed"); // 源被修改 → 现在算 CacheKey 会得到新指纹

            ArtifactLocator.CleanRegistered();
            Assert.False(Directory.Exists(subsB));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void 超长源文件名_产物组件截断在上限内()
    {
        using var dir = new TempDir();
        var longName = new string('长', 200) + ".mkv";
        var src = Path.Combine(dir.Path, longName);
        File.WriteAllBytes(src, [1, 2, 3]);

        var target = ArtifactLocator.ForProduction(src, "mp4.hls", null);
        // 单路径组件必须低于 Windows 255 上限：{显示名}.{40位键}.mp4.hls.tmp
        var component = Path.GetFileName(target.TempPath);
        Assert.True(component.Length < 255, $"产物组件超长: {component.Length}");
        // 保留可读前缀（截断而非纯哈希），且同指纹同变体路径稳定（可复用命中）
        Assert.StartsWith("长长长", Path.GetFileName(Path.GetDirectoryName(target.ArtifactPath)));
        Assert.Equal(target.ArtifactPath, ArtifactLocator.ForProduction(src, "mp4.hls", null).ArtifactPath);
    }

    [Fact]
    public void 超长源文件名_截断不从代理对中间切开()
    {
        using var dir = new TempDir();
        // 139 个汉字 + emoji（占 2 个码元）→ 代理对正好卡在 140 截断边界（139=高代理）
        var name = new string('长', 139) + "😀" + ".mkv";
        var src = Path.Combine(dir.Path, name);
        File.WriteAllBytes(src, [1, 2, 3]);

        var target = ArtifactLocator.ForProduction(src, "mp4.hls", null);
        // 产物目录名 = {截断显示名}.{40位键}.mp4.hls，取显示名部分断言结尾不是孤立高代理
        var dirName = Path.GetFileName(Path.GetDirectoryName(target.ArtifactPath))!;
        var key = CacheKey.For(src);
        var marker = $".{key}.";
        var stem = dirName[..dirName.IndexOf(marker, StringComparison.Ordinal)];
        Assert.False(char.IsHighSurrogate(stem[^1]), "截断后显示名以孤立高代理结尾");
        Assert.False(char.IsLowSurrogate(stem[^1]), "截断后显示名以孤立低代理结尾");
        // 回退一个码元后仍在可读前缀范围内（不是空名/纯 "media" 兑底）
        Assert.StartsWith("长长长", stem);

        // 代理对完整落在 cut 内（138 汉字 + emoji + 填充）时不得误截
        var name2 = new string('长', 138) + "😀" + new string('x', 20) + ".mkv";
        var src2 = Path.Combine(dir.Path, name2);
        File.WriteAllBytes(src2, [1, 2, 3]);
        var target2 = ArtifactLocator.ForProduction(src2, "mp4.hls", null);
        var dirName2 = Path.GetFileName(Path.GetDirectoryName(target2.ArtifactPath))!;
        var stem2 = dirName2[..dirName2.IndexOf($".{CacheKey.For(src2)}.", StringComparison.Ordinal)];
        Assert.False(char.IsHighSurrogate(stem2[^1]), "完整代理对被误截");
    }

    [Fact]
    public void SafeStem_超长名截断到安全预算_短名原样()
    {
        Assert.Equal("movie.mkv", ArtifactLocator.SafeStem("movie.mkv"));
        var longName = new string('a', 500);
        var stem = ArtifactLocator.SafeStem(longName);
        Assert.Equal(140, stem.Length);
    }

    [Fact]
    public void SafeStem_代理对卡在边界时回退一个码元()
    {
        // 139 个 'a' + emoji（代理对占 140/141 两个码元，cut 到 140 会把低代理截掉）
        var stem = ArtifactLocator.SafeStem(new string('a', 139) + char.ConvertFromUtf32(0x1F389));
        Assert.Equal(139, stem.Length);
        Assert.False(char.IsHighSurrogate(stem[^1]), "孤立高代理泄漏到显示名");
        Assert.False(char.IsLowSurrogate(stem[^1]), "孤立低代理泄漏到显示名");
    }

    [Fact]
    public void SafeStem_截断后修剪尾部点与空格()
    {
        // 137 'a' + 4 个点 = 141 > 140 → cut 后尾部是 "..."，修剪回 137
        var stem = ArtifactLocator.SafeStem(new string('a', 137) + "....");
        Assert.Equal(137, stem.Length);
        Assert.All(stem, c => Assert.Equal('a', c));
        // 尾部点+空格混合：cut 边界落在 "..  " 上，修剪回 136 个 'a'
        var stem2 = ArtifactLocator.SafeStem(new string('a', 136) + "..  " + new string('b', 3));
        Assert.Equal(136, stem2.Length);
        Assert.All(stem2, c => Assert.Equal('a', c));
    }

    [Fact]
    public void SafeStem_纯点纯空格名兑底为media()
    {
        Assert.Equal("media", ArtifactLocator.SafeStem(new string('.', 200)));
        Assert.Equal("media", ArtifactLocator.SafeStem(new string(' ', 200)));
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
