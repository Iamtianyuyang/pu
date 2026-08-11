using System.Security.Cryptography;
using System.Text;

namespace Pu.Core.Cache;

/// <summary>
/// 缓存键：sha1(绝对路径 | 大小 | 修改时间)。
/// 源文件任何变化 → 键变化 → 自动重转，不会拿到过期产物（方案.md 第八节）。
/// </summary>
public static class CacheKey
{
    public static string For(string filePath)
    {
        var fi = new FileInfo(filePath);
        var raw = $"{Path.GetFullPath(filePath)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static string ArtifactDirFor(string filePath)
        => Path.Combine(CachePaths.RootDir(), For(filePath));
}
