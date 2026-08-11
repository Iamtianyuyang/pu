using System.Buffers.Binary;
using System.Text;
using Pu.Core.Probe;
using Xunit;

namespace Pu.Core.Tests;

public class Mp4BoxesTests
{
    [Fact]
    public void Moov在Mdat前_是FastStart()
    {
        using var dir = new TempDir();
        var path = WriteBoxes(dir.Path, ("ftyp", 32), ("moov", 24), ("mdat", 64));
        Assert.True(Mp4Boxes.IsFastStart(path));
    }

    [Fact]
    public void Mdat在Moov前_不是FastStart()
    {
        using var dir = new TempDir();
        var path = WriteBoxes(dir.Path, ("ftyp", 32), ("mdat", 64), ("moov", 24));
        Assert.False(Mp4Boxes.IsFastStart(path));
    }

    [Fact]
    public void 没有Moov_不是FastStart()
    {
        using var dir = new TempDir();
        var path = WriteBoxes(dir.Path, ("ftyp", 32), ("mdat", 64));
        Assert.False(Mp4Boxes.IsFastStart(path));
    }

    [Fact]
    public void Box64位扩展尺寸_可解析()
    {
        using var dir = new TempDir();
        // largesize > uint.MaxValue，走 64 位路径（稀疏文件，不占实际磁盘）
        var path = WriteBoxes(dir.Path, ("ftyp", 32), ("moov", 24), ("mdat", (1L << 32) + 64));
        Assert.True(Mp4Boxes.IsFastStart(path));
    }

    [Fact]
    public void 非Mp4文件_返回False不抛异常()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "junk.bin");
        File.WriteAllText(path, "这不是 MP4，是纯文本");
        Assert.False(Mp4Boxes.IsFastStart(path));
    }

    /// <summary>写一串顶层 box。size 含 box 头。</summary>
    private static string WriteBoxes(string dir, params (string Type, long Size)[] boxes)
    {
        var path = Path.Combine(dir, "boxes.mp4");
        using var fs = File.Create(path);
        foreach (var (type, size) in boxes)
        {
            var start = fs.Position;
            if (size <= uint.MaxValue)
            {
                var header = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
                Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
                fs.Write(header);
            }
            else
            {
                var header = new byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(header, 1); // size == 1 → largesize
                Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)size);
                fs.Write(header);
            }
            fs.SetLength(start + size); // 稀疏填充
            fs.Position = start + size;
        }
        return path;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
