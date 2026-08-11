namespace Pu.Core.Tests;

/// <summary>测试环境助手：临时文件一律落在项目 tmp/ 下（项目写入边界）。</summary>
public static class TestEnv
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（Pu.sln）");
    }

    public static string NewTestDir()
    {
        var dir = Path.Combine(RepoRoot(), "tmp", "tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static bool HasFfmpeg => FindOnPath("ffmpeg") is not null && FindOnPath("ffprobe") is not null;

    private static string? FindOnPath(string name)
    {
        var candidates = new[] { name, name + ".exe" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var c in candidates)
            {
                var p = Path.Combine(dir, c);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
