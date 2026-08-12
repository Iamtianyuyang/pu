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
/// 产物路径内嵌源指纹+变体键（.pu/{name}.{key}.{ext}）：源替换/策略变更 → 新键 → 新路径，
/// 不覆盖旧产物，旧播放会话不受影响。
/// .pu\ 目录置 hidden 属性；产物路径登记进 sidecars.log，`pu --clean` 统一清除。
/// 字幕等副产物按源指纹隔离在 .pu/{指纹}/subs，清单只存不可逆字幕键（不存绝对路径，防 HLS 目录泄露）。
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
        var sidecar = SidecarArtifactPath(sourcePath, outputExtension, variant);
        if (File.Exists(sidecar) && ManifestMatches(sidecar, sourcePath, variant))
            return sidecar;

        var central = CentralArtifactPath(sourcePath, outputExtension, variant);
        // 只认正式路径：.tmp 是生产中/崩溃残留，一律不算可复用
        if (File.Exists(central)) return central;
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
            var artifact = SidecarArtifactPath(sourcePath, outputExtension, variant);
            return new ArtifactTarget(artifact,
                IsHlsLayout(outputExtension) ? ArtifactDirOf(artifact) + ".tmp" : artifact + ".tmp",
                sidecarDir, Sidecar: true);
        }
        // 中央缓存同样先写 .tmp 再改名：硬崩溃（断电/杀进程）残留的半截文件不会在下次被当有效产物复用。
        // HLS 用整目录 {key}/out.mp4.hls.tmp，非 HLS 用文件 {key}/out.mp4.tmp。
        var central = CentralArtifactPath(sourcePath, outputExtension, variant);
        var temp = IsHlsLayout(outputExtension) ? ArtifactDirOf(central) + ".tmp" : central + ".tmp";
        return new ArtifactTarget(central, temp, CacheKey.ArtifactDirFor(sourcePath, variant), Sidecar: false);
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

    /// <summary>从产物路径反推 .pu 目录（HLS 产物向上两级，普通产物向上一级）；不在 .pu 下返回 null。</summary>
    public static string? SidecarDirOf(string artifactPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        if (dir is null) return null;
        if (string.Equals(Path.GetFileName(dir), SidecarDirName, StringComparison.OrdinalIgnoreCase))
            return dir;
        var parent = Path.GetDirectoryName(dir);
        return parent is not null
            && string.Equals(Path.GetFileName(parent), SidecarDirName, StringComparison.OrdinalIgnoreCase)
            ? parent
            : null;
    }

    /// <summary>HLS 布局：扩展名以 .hls 结尾时，产物是 {name}.{ext}/index.m3u8 目录式。</summary>
    public static bool IsHlsLayout(string outputExtension)
        => outputExtension.EndsWith(".hls", StringComparison.OrdinalIgnoreCase);

    /// <summary>HLS 产物的目录（{name}.mp4.hls）。</summary>
    public static string ArtifactDirOf(string artifactPath) => Path.GetDirectoryName(artifactPath)!;

    /// <summary>生产成功后写复用清单（与产物同名的 .json）。
    /// 只存不可逆字幕键（sha1 指纹），不存绝对源路径：HLS 产物目录内文件可被 /hls/ 访问，
    /// 清单泄露也不暴露宿主机路径；clean 按清单键删字幕，源文件删除/修改都不受影响。</summary>
    public static void WriteManifest(string artifactPath, string sourcePath, string? variant)
    {
        try
        {
            var fi = new FileInfo(sourcePath);
            var json = JsonSerializer.Serialize(new Manifest(fi.Length, fi.LastWriteTimeUtc.Ticks, variant,
                CacheKey.For(sourcePath))); // 生产时的字幕目录键
            File.WriteAllText(ManifestPath(artifactPath), json);
        }
        catch { /* 清单写失败只是下次重转 */ }
    }

    /// <summary>登记就地产物路径，供 pu --clean 统一清除。
    /// 登记前去重：同路径只写一行，避免长期使用后 sidecars.log 膨胀、--clean 重复删。</summary>
    public static void Register(string artifactPath)
    {
        try
        {
            lock (RegistryLock)
            {
                Directory.CreateDirectory(FfmpegLocator.ConfigDir);
                var known = File.Exists(RegistryPath)
                    ? File.ReadAllLines(RegistryPath)
                        .Select(l => l.Trim()).Where(l => l.Length > 0)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (known.Add(artifactPath))
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
            var dir = Path.GetDirectoryName(artifact);
            // 字幕副产物：按生产时键隔离在 .pu/{键}/subs（清单里存的不可逆字幕键，不依赖源文件存在）；
            // 旧版无键的清单 → 整删 .pu/subs 兜底。
            // 必须先读清单再删产物：HLS 清单在产物目录里，目录删掉后就读不到了。
            var subsKey = ManifestSubsKey(artifact);
            // HLS 产物（index.m3u8）→ 整个 {name}.mp4.hls 目录删掉（分片都在里面）
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
            var subsParent = Path.GetFileName(dir) == SidecarDirName ? dir : Path.GetDirectoryName(dir);
            if (subsParent is not null && Path.GetFileName(subsParent) == SidecarDirName)
            {
                var subsDir = subsKey is { Length: > 0 } k
                    ? Path.Combine(subsParent, k)
                    : Path.Combine(subsParent, "subs");
                try { Directory.Delete(subsDir, recursive: true); } catch { }
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

    private static string SidecarArtifactPath(string sourcePath, string outputExtension, string? variant)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        // 产物名内嵌源指纹+变体键：源替换/策略变更 → 新键 → 新路径，旧产物与旧播放会话不受影响
        var key = variant is null ? CacheKey.For(sourcePath) : CacheKey.For(sourcePath, variant);
        var name = SafeStem(Path.GetFileName(sourcePath));
        var basePath = Path.Combine(dir, SidecarDirName,
            $"{name}.{key}.{outputExtension}");
        return IsHlsLayout(outputExtension)
            ? Path.Combine(basePath, "index.m3u8") // {name}.{key}.mp4.hls/index.m3u8
            : basePath;
    }

    /// <summary>产物显示名：完整源文件名 + 40 位指纹键 + 扩展名可能突破 Windows
    /// 单路径组件上限（255），创建 .tmp 文件或 HLS 目录时直接失败。
    /// 截断到安全预算并保留可读前缀；避免从代理对中间截断（emoji 等补充平面字符）；
    /// 去掉截断后可能出现的尾部点/空格（Windows 会静默修剪它们，两个名字会撞到同一个组件）。</summary>
    internal static string SafeStem(string stem)
    {
        const int maxStem = 140; // 140 + '.' + 40 位键 + ".mp4.hls"(8) + ".tmp"(4) ≈ 194 < 255
        if (stem.Length <= maxStem) return stem;
        var cut = stem[..maxStem];
        // 别从代理对中间截断：边界是低代理说明代理对完整落在 cut 内，不需要动；
        // 边界是孤立高代理说明低半被截掉了（代理对卡在 139/140），回退一个码元
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        cut = cut.TrimEnd(' ', '.');
        return cut.Length > 0 ? cut : "media";
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

    /// <summary>字幕目录键记录在清单里：--clean 直接按生产时键删除，不依赖源文件仍然存在（旧清单无键 → 兜底整删）。</summary>
    private static string? ManifestSubsKey(string artifactPath)
    {
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath(artifactPath)));
            return m?.SubsKey;
        }
        catch
        {
            return null;
        }
    }

    private sealed record Manifest(long Size, long MtimeUtcTicks, string? Variant, string? SubsKey = null);
}
