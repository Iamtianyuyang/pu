using System.Collections.Concurrent;
using System.Text.Json;
using Pu.Core.Common;

namespace Pu.Core.Cache;

/// <summary>产物落点：正式路径 / 生产期临时路径 / 工作目录（字幕等副产物也放这）/ 是否就地产物。</summary>
public sealed record ArtifactTarget(string ArtifactPath, string TempPath, string WorkDir, bool Sidecar);

/// <summary>
/// 产物落点决策：优先源文件旁的 .pu\ 子目录——就地操作，中央缓存不再复制第二份；
/// 源目录不可写（只读 NAS / 权限 / 光盘）→ 回退中央缓存（%LOCALAPPDATA%\Pu\cache，20GB LRU）。
/// 就地产物先写 .tmp 再改名（不留半截 mp4），配套 .json 清单校验复用（源大小 | mtime | 策略变体）；
/// .pu\ 目录置 hidden 属性；产物路径登记进 sidecars.log，`pu --clean` 统一清除。
/// </summary>
public static class ArtifactLocator
{
    public const string SidecarDirName = ".pu";

    private static readonly ConcurrentDictionary<string, bool> WritableDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RegistryLock = new();

    private static string RegistryPath => Path.Combine(FfmpegLocator.ConfigDir, "sidecars.log");

    /// <summary>测试注入：覆盖目录可写性判定。</summary>
    internal static Func<string, bool>? WritableOverride;

    internal static void ClearProbeCache() => WritableDirs.Clear();

    /// <summary>命中可复用的产物：就地产物（清单校验通过）或中央缓存旧产物；都没有 → null。</summary>
    public static string? TryGetReusable(string sourcePath, string outputExtension, string? variant)
    {
        var sidecar = SidecarArtifactPath(sourcePath, outputExtension);
        if (File.Exists(sidecar) && ManifestMatches(sidecar, sourcePath, variant))
            return sidecar;

        var central = CentralArtifactPath(sourcePath, outputExtension, variant);
        if (File.Exists(central)) return central; // 旧缓存时代的产物接着用（由 LRU 管理）
        return null;
    }

    /// <summary>决定新产物的落点（优先就地）。
    /// HLS（扩展名 "mp4.hls"）：产物 = 目录/{name}.mp4.hls/index.m3u8，临时 = 同目录 + .tmp（整目录）。</summary>
    public static ArtifactTarget ForProduction(string sourcePath, string outputExtension, string? variant)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var sidecarDir = Path.Combine(dir, SidecarDirName);
        if (IsWritable(dir, sidecarDir))
        {
            var artifact = SidecarArtifactPath(sourcePath, outputExtension);
            return new ArtifactTarget(artifact,
                IsHlsLayout(outputExtension) ? ArtifactDirOf(artifact) + ".tmp" : artifact + ".tmp",
                sidecarDir, Sidecar: true);
        }
        var central = CentralArtifactPath(sourcePath, outputExtension, variant);
        return new ArtifactTarget(central, central, Path.GetDirectoryName(central)!, Sidecar: false);
    }

    public static bool IsSidecarPath(string artifactPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        if (string.Equals(Path.GetFileName(dir), SidecarDirName, StringComparison.OrdinalIgnoreCase))
            return true;
        // HLS 产物在 {name}.mp4.hls/ 里，往上两级的 .pu 才算就地产物
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), SidecarDirName,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>HLS 布局：扩展名以 .hls 结尾时，产物是 {name}.{ext}/index.m3u8 目录式。</summary>
    public static bool IsHlsLayout(string outputExtension)
        => outputExtension.EndsWith(".hls", StringComparison.OrdinalIgnoreCase);

    /// <summary>HLS 产物的目录（{name}.mp4.hls）。</summary>
    public static string ArtifactDirOf(string artifactPath) => Path.GetDirectoryName(artifactPath)!;

    /// <summary>生产成功后写复用清单（与产物同名的 .json）。</summary>
    public static void WriteManifest(string artifactPath, string sourcePath, string? variant)
    {
        try
        {
            var fi = new FileInfo(sourcePath);
            var json = JsonSerializer.Serialize(new Manifest(fi.Length, fi.LastWriteTimeUtc.Ticks, variant));
            File.WriteAllText(ManifestPath(artifactPath), json);
        }
        catch { /* 清单写失败只是下次重转 */ }
    }

    /// <summary>登记就地产物路径，供 pu --clean 统一清除。</summary>
    public static void Register(string artifactPath)
    {
        try
        {
            lock (RegistryLock)
            {
                Directory.CreateDirectory(FfmpegLocator.ConfigDir);
                File.AppendAllText(RegistryPath, artifactPath + Environment.NewLine);
            }
        }
        catch { }
    }

    /// <summary>删除所有登记过的就地产物（及清单和空的 .pu 目录），返回释放字节数。</summary>
    public static long CleanRegistered()
    {
        if (!File.Exists(RegistryPath)) return 0;
        string[] lines;
        lock (RegistryLock)
        {
            try { lines = File.ReadAllLines(RegistryPath); }
            catch { return 0; }
        }

        long freed = 0;
        foreach (var line in lines)
        {
            var artifact = line.Trim();
            if (artifact.Length == 0) continue;
            // HLS 产物（index.m3u8）→ 整个 {name}.mp4.hls 目录删掉（分片都在里面）
            var dir = Path.GetDirectoryName(artifact);
            if (dir is not null && Path.GetFileName(artifact) == "index.m3u8" && Directory.Exists(dir))
            {
                freed += DirSize(dir);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            else
            {
                try { freed += new FileInfo(artifact).Length; File.Delete(artifact); } catch { }
            }
            try { File.Delete(ManifestPath(artifact)); } catch { }
            // 字幕副产物 .pu/subs（HLS 产物目录 {name}.mp4.hls 需向上取一级到 .pu）
            var subsParent = Path.GetFileName(dir) == SidecarDirName ? dir : Path.GetDirectoryName(dir);
            if (subsParent is not null && Path.GetFileName(subsParent) == SidecarDirName)
            {
                try { Directory.Delete(Path.Combine(subsParent, "subs"), recursive: true); } catch { }
            }
            // 空的 .pu 一并删（目录删完才算空）
            try
            {
                if (dir is not null && Path.GetFileName(dir) != SidecarDirName
                    && Directory.Exists(Path.GetDirectoryName(dir)))
                    dir = Path.GetDirectoryName(dir);
                if (dir is not null && Directory.Exists(dir)
                    && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }
        try { File.Delete(RegistryPath); } catch { }
        return freed;
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                total += new FileInfo(f).Length;
        }
        catch { }
        return total;
    }

    private static string SidecarArtifactPath(string sourcePath, string outputExtension)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var basePath = Path.Combine(dir, SidecarDirName, $"{Path.GetFileName(sourcePath)}.{outputExtension}");
        return IsHlsLayout(outputExtension)
            ? Path.Combine(basePath, "index.m3u8") // {name}.mp4.hls/index.m3u8
            : basePath;
    }

    private static string CentralArtifactPath(string sourcePath, string outputExtension, string? variant)
        => IsHlsLayout(outputExtension)
            ? Path.Combine(CacheKey.ArtifactDirFor(sourcePath, variant), $"out.{outputExtension}", "index.m3u8")
            : Path.Combine(CacheKey.ArtifactDirFor(sourcePath, variant), $"out.{outputExtension}");

    private static string ManifestPath(string artifactPath) => artifactPath + ".json";

    private static bool IsWritable(string sourceDir, string sidecarDir)
    {
        if (WritableOverride is { } probe) return probe(sourceDir);
        return WritableDirs.GetOrAdd(sourceDir, _ =>
        {
            try
            {
                Directory.CreateDirectory(sidecarDir);
                var probeFile = Path.Combine(sidecarDir, $".probe-{Environment.ProcessId}");
                File.WriteAllBytes(probeFile, [0]);
                File.Delete(probeFile);
                try { File.SetAttributes(sidecarDir, File.GetAttributes(sidecarDir) | FileAttributes.Hidden); }
                catch { /* 隐藏属性失败不影响使用 */ }
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static bool ManifestMatches(string artifactPath, string sourcePath, string? variant)
    {
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath(artifactPath)));
            if (m is null) return false;
            var fi = new FileInfo(sourcePath);
            return m.Size == fi.Length
                && m.MtimeUtcTicks == fi.LastWriteTimeUtc.Ticks
                && m.Variant == variant;
        }
        catch
        {
            return false;
        }
    }

    private sealed record Manifest(long Size, long MtimeUtcTicks, string? Variant);
}
