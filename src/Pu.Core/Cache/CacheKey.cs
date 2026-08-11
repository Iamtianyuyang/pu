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

    /// <summary>带变体的键：同一路径但转码策略/编码器不同 → 不同缓存目录（防止命中旧策略产物）。</summary>
    public static string For(string filePath, string variant)
    {
        var fi = new FileInfo(filePath);
        var raw = $"{Path.GetFullPath(filePath)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{variant}";
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static string ArtifactDirFor(string filePath, string? variant = null)
        => Path.Combine(CachePaths.RootDir(), variant is null ? For(filePath) : For(filePath, variant));
}
