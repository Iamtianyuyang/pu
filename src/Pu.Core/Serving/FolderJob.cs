using System.Collections.Concurrent;

namespace Pu.Core.Serving;

/// <summary>文件夹会话：列表页 /f/{token}。文件按需点开 → 才创建媒体任务（懒加载，不预转码）。
/// 同一文件夹重复右键 → 复用同一会话并刷新列表（_folders 不随重复提交膨胀）。</summary>
public sealed class FolderJob
{
    private readonly object _gate = new();
    private IReadOnlyList<FolderFile> _files = [];
    private bool _truncated;
    private readonly ConcurrentDictionary<int, string> _opened = new();

    public required string Token { get; init; }
    public required string FolderPath { get; init; }
    public required string Title { get; init; }

    /// <summary>会话创建时刻（_folders 上限淘汰最老会话用；复用刷新不更新）。</summary>
    public long CreatedTicks { get; init; } = DateTime.UtcNow.Ticks;

    /// <summary>扫描快照（锁保护：刷新与 HTTP 读取并发）。</summary>
    public IReadOnlyList<FolderFile> Files { get { lock (_gate) return _files; } }

    /// <summary>扫描因达到文件数上限被截断：页面据此提示「仅显示前 500 个」。</summary>
    public bool Truncated { get { lock (_gate) return _truncated; } }

    /// <summary>复用路径：同一文件夹重新提交时刷新列表（新加的文件可见）。
    /// 重置已打开映射：列表变化后索引可能错位，旧页面的打开请求由 index 校验兜底（404）。</summary>
    public void Refresh(IReadOnlyList<FolderFile> files, bool truncated)
    {
        lock (_gate)
        {
            _files = files;
            _truncated = truncated;
            _opened.Clear();
        }
    }

    public string? OpenedToken(int index)
    {
        lock (_gate) return _opened.TryGetValue(index, out var t) ? t : null;
    }

    public void MarkOpened(int index, string token)
    {
        lock (_gate) _opened[index] = token;
    }
}
