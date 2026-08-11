using Pu.Core;
using Pu.Core.Serving;
using Xunit;

namespace Pu.Core.Tests;

public class FolderScanTests
{
    [Fact]
    public void 递归扫描_只收媒体文件_按名排序()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "S01"));
        File.WriteAllBytes(Path.Combine(dir.Path, "b.mkv"), [1]);
        File.WriteAllBytes(Path.Combine(dir.Path, "a.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(dir.Path, "notes.txt"), [1]);   // 非媒体
        File.WriteAllBytes(Path.Combine(dir.Path, "cover.jpg"), [1]);   // 非媒体
        File.WriteAllBytes(Path.Combine(dir.Path, "S01", "c.avi"), [1]);
        File.WriteAllBytes(Path.Combine(dir.Path, "S01", "sub.srt"), [1]); // 非媒体

        var files = FolderScan.Scan(dir.Path, MediaExtensions.Defaults);

        Assert.Equal(3, files.Count);
        Assert.Equal(["a.mp4", "b.mkv", "c.avi"], files.Select(f => f.Name));
        Assert.Equal(new[] { 0, 1, 2 }, files.Select(f => f.Index));
        Assert.All(files, f => Assert.True(f.SizeBytes > 0));
    }

    [Fact]
    public void 空文件夹_返回空列表()
    {
        using var dir = new TempDir();
        var files = FolderScan.Scan(dir.Path, MediaExtensions.Defaults);
        Assert.Empty(files);
    }

    [Fact]
    public void 超过上限_截断()
    {
        using var dir = new TempDir();
        for (int i = 0; i < 20; i++)
            File.WriteAllBytes(Path.Combine(dir.Path, $"{i:D2}.mp4"), [1]);

        var files = FolderScan.Scan(dir.Path, MediaExtensions.Defaults, maxFiles: 10);
        Assert.Equal(10, files.Count);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
