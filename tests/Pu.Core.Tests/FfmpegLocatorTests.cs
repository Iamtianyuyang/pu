using Pu.Core.Common;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>ffmpeg 定位链测试：配置文件 → PATH。用 PU_CONFIG_DIR 隔离，不碰真实配置。</summary>
public class FfmpegLocatorTests
{
    [Fact]
    public void 配置文件指向有效exe_优先于PATH()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            var fake = Path.Combine(dir.Path, "ffmpeg.exe");
            File.WriteAllBytes(fake, [1]);
            File.WriteAllText(Path.Combine(dir.Path, "config.json"),
                $"{{\"ffmpeg\":\"{fake.Replace("\\", "\\\\")}\"}}");
            Assert.Equal(fake, FfmpegLocator.Exe);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void 配置指向目录_自动补exe名()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            var fake = Path.Combine(dir.Path, "ffmpeg.exe");
            File.WriteAllBytes(fake, [1]);
            var binDir = Path.Combine(dir.Path, "bin");
            Directory.CreateDirectory(binDir);
            File.Copy(fake, Path.Combine(binDir, "ffmpeg.exe"));
            File.WriteAllText(Path.Combine(dir.Path, "config.json"),
                $"{{\"ffmpeg\":\"{binDir.Replace("\\", "\\\\")}\"}}");
            Assert.Equal(Path.Combine(binDir, "ffmpeg.exe"), FfmpegLocator.Exe);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void 无配置文件_从PATH找到()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path); // 空目录 → 无配置
        try
        {
            var exe = FfmpegLocator.Exe;
            Assert.EndsWith("ffmpeg.exe", exe, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(FfmpegLocator.ProbeExe));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PU_CONFIG_DIR", old);
        }
    }

    [Fact]
    public void 配置损坏_退回PATH不抛异常()
    {
        using var dir = new TempDir();
        var old = Environment.GetEnvironmentVariable("PU_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PU_CONFIG_DIR", dir.Path);
        try
        {
            File.WriteAllText(Path.Combine(dir.Path, "config.json"), "{ 这不是 JSON");
            Assert.False(string.IsNullOrWhiteSpace(FfmpegLocator.Exe));
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
}
