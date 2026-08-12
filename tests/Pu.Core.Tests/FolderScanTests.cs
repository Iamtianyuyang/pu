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

    [Fact]
    public void 超过上限_标记截断()
    {
        using var dir = new TempDir();
        for (int i = 0; i < 20; i++)
            File.WriteAllBytes(Path.Combine(dir.Path, $"{i:D2}.mp4"), [1]);

        var truncated = FolderScan.ScanDetailed(dir.Path, MediaExtensions.Defaults, maxFiles: 10);
        Assert.Equal(10, truncated.Files.Count);
        Assert.True(truncated.Truncated, "还有更多文件未列出 → 应标记截断");

        // 恰好满上限（无截断）
        using var exact = new TempDir();
        for (int i = 0; i < 10; i++)
            File.WriteAllBytes(Path.Combine(exact.Path, $"{i:D2}.mp4"), [1]);
        var notTruncated = FolderScan.ScanDetailed(exact.Path, MediaExtensions.Defaults, maxFiles: 10);
        Assert.Equal(10, notTruncated.Files.Count);
        Assert.False(notTruncated.Truncated);
    }

    [Fact]
    public void 自然排序_EP2排在EP10前()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "EP10.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(dir.Path, "EP2.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(dir.Path, "EP1.mp4"), [1]);
        File.WriteAllBytes(Path.Combine(dir.Path, "EP01.mp4"), [1]); // 数值相等（01 vs 1）：较长者排后

        var files = FolderScan.Scan(dir.Path, MediaExtensions.Defaults);

        Assert.Equal(["EP1.mp4", "EP01.mp4", "EP2.mp4", "EP10.mp4"], files.Select(f => f.Name));
    }

    [Fact]
    public void 截断发生在排序之后_保留自然序前N个()
    {
        using var dir = new TempDir();
        for (int i = 1; i <= 20; i++)
            File.WriteAllBytes(Path.Combine(dir.Path, $"EP{i}.mp4"), [1]);

        // 无论枚举顺序如何，保留下来的必须是自然序前 10 个（EP1–EP10），而非枚举顺序的任意 10 个
        var r = FolderScan.ScanDetailed(dir.Path, MediaExtensions.Defaults, maxFiles: 10);
        Assert.Equal(10, r.Files.Count);
        Assert.True(r.Truncated);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"EP{i}.mp4"), r.Files.Select(f => f.Name));
        Assert.Equal(Enumerable.Range(0, 10), r.Files.Select(f => f.Index));
    }

    [Fact]
    public void 深度超限_同样标记截断()
    {
        using var dir = new TempDir();
        var deep = dir.Path;
        for (int i = 0; i < 10; i++) deep = Path.Combine(deep, $"d{i}");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "hidden.mp4"), [1]);

        var shallow = FolderScan.ScanDetailed(dir.Path, MediaExtensions.Defaults, maxDepth: 3);
        Assert.Empty(shallow.Files);
        Assert.True(shallow.Truncated, "深度超限处还有文件被跳过 → 应提示列表不完整");
    }

    [Fact]
    public void 超过深度上限_不再深入()
    {
        using var dir = new TempDir();
        var deep = dir.Path;
        for (int i = 0; i < 10; i++) deep = Path.Combine(deep, $"d{i}");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "hidden.mp4"), [1]);

        // 深度限制 3 → 扫不到 10 层下的文件
        var shallow = FolderScan.Scan(dir.Path, MediaExtensions.Defaults, maxDepth: 3);
        Assert.Empty(shallow);
        // 放宽深度 → 能找到
        var deepScan = FolderScan.Scan(dir.Path, MediaExtensions.Defaults, maxDepth: 20);
        var hit = Assert.Single(deepScan);
        Assert.Equal("hidden.mp4", hit.Name);
    }

    [Fact]
    public void Junction循环_不死循环()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "a.mp4"), [1]);
        // mklink /J 建 junction：sub → 指向自身父目录（循环）；junction 无需管理员权限
        var link = Path.Combine(dir.Path, "loop");
        var mk = TestEnv.RunCmd("mklink", "/J", link, dir.Path);
        if (mk.ExitCode != 0) return; // 环境不支持 junction（如 CI 沙箱）→ 跳过

        var files = FolderScan.Scan(dir.Path, MediaExtensions.Defaults);

        // 正常返回且不重复扫到循环里的文件（junction 本身被跳过，只出根目录的 a.mp4）
        var hit = Assert.Single(files);
        Assert.Equal("a.mp4", hit.Name);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
