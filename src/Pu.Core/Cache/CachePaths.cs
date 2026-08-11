namespace Pu.Core.Cache;

/// <summary>缓存根目录（方案.md 第八节：%LOCALAPPDATA%\Pu\cache，可用环境变量覆盖）。</summary>
public static class CachePaths
{
    public static string RootDir()
    {
        var env = Environment.GetEnvironmentVariable("PU_CACHE_DIR");
        return string.IsNullOrWhiteSpace(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pu", "cache")
            : env;
    }
}
