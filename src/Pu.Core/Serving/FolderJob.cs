using System.Collections.Concurrent;

namespace Pu.Core.Serving;

/// <summary>文件夹会话：列表页 /f/{token}。文件按需点开 → 才创建媒体任务（懒加载，不预转码）。</summary>
public sealed class FolderJob
{
    private readonly ConcurrentDictionary<int, string> _opened = new();

    public required string Token { get; init; }
    public required string FolderPath { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<FolderFile> Files { get; init; }

    /// <summary>扫描因达到文件数上限被截断：页面据此提示「仅显示前 500 个」。</summary>
    public bool Truncated { get; init; }

    public string? OpenedToken(int index) => _opened.TryGetValue(index, out var t) ? t : null;
    public void MarkOpened(int index, string token) => _opened[index] = token;
}
