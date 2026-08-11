using System.Buffers.Binary;
using System.Text;

namespace Pu.Core.Probe;

/// <summary>MP4 顶层 box 扫描：判断 moov 是否在 mdat 之前（faststart）。</summary>
public static class Mp4Boxes
{
    /// <summary>moov 在 mdat 之前即视为 faststart。非 MP4 / 无法解析时返回 false。</summary>
    public static bool IsFastStart(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long moov = -1, mdat = -1;
            long pos = 0;
            var sizeBuf = new byte[8];
            var header = new byte[8];
            while (pos + 8 <= fs.Length)
            {
                fs.Position = pos;
                if (!ReadExactly(fs, header, 8)) break;
                var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header, 4, 4);

                long boxSize;
                if (size32 == 1)
                {
                    // 64 位扩展尺寸
                    if (!ReadExactly(fs, sizeBuf, 8)) break;
                    boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(sizeBuf);
                }
                else if (size32 == 0)
                {
                    boxSize = fs.Length - pos; // box 延伸到文件尾
                }
                else
                {
                    boxSize = size32;
                }

                if (type == "moov") moov = pos;
                else if (type == "mdat") mdat = pos;
                if (boxSize <= 0) break;
                pos += boxSize;
            }
            return moov >= 0 && (mdat < 0 || moov < mdat);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool ReadExactly(FileStream fs, Span<byte> buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buffer[read..count]);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
