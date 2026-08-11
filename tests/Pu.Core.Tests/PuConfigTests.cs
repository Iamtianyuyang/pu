using Pu.Core.Cache;
using Pu.Core.Common;
using Pu.Core.Planning;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>转码策略配置（config.json 的 transcode 键）与缓存键变体。</summary>
public class PuConfigTests
{
    private static T WithConfigDir<T>(string? configJson, Func<string, T> run)
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            if (configJson is not null)
                File.WriteAllText(Path.Combine(dir.Path, "config.json"), configJson);
            return run(dir.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void 无配置文件_默认最快看到视频()
        => Assert.Equal(TranscodePolicy.Auto, WithConfigDir(null, _ => PuConfig.TranscodePolicy));

    [Fact]
    public void 配置always_强制转码()
        => Assert.Equal(TranscodePolicy.ForceGpu,
            WithConfigDir("{\"transcode\":\"always\"}", _ => PuConfig.TranscodePolicy));

    [Fact]
    public void 配置auto_最快看到视频()
        => Assert.Equal(TranscodePolicy.Auto,
            WithConfigDir("{\"transcode\":\"auto\"}", _ => PuConfig.TranscodePolicy));

    [Fact]
    public void 配置损坏_回退默认()
        => Assert.Equal(TranscodePolicy.Auto,
            WithConfigDir("not json at all", _ => PuConfig.TranscodePolicy));

    [Fact]
    public void 缓存键_变体隔离不同策略产物()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "a.mp4");
        File.WriteAllBytes(file, [1, 2, 3]);

        var plain = CacheKey.ArtifactDirFor(file);
        var gpu = CacheKey.ArtifactDirFor(file, "gpu:h264_nvenc");
        var otherGpu = CacheKey.ArtifactDirFor(file, "gpu:h264_amf");

        Assert.NotEqual(plain, gpu);
        Assert.NotEqual(gpu, otherGpu);
        // 同参数稳定：缓存才能命中
        Assert.Equal(gpu, CacheKey.ArtifactDirFor(file, "gpu:h264_nvenc"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
