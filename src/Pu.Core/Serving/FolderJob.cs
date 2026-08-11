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

    public string? OpenedToken(int index) => _opened.TryGetValue(index, out var t) ? t : null;
    public void MarkOpened(int index, string token) => _opened[index] = token;
}
