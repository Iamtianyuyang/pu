namespace Pu.Core.Serving;

public enum JobState
{
    Transcoding, // 转码中（含排队）
    Serving,     // 可播
    Failed,      // 失败
}

public sealed record SubtitleFile(int StreamIndex, string Codec, string Language, string Title, string VttPath);

/// <summary>
/// 一个媒体任务：右键一个文件 → 一个 job。状态页轮询的就是它。
/// 转码在后台线程跑，HTTP 线程只读状态（锁保护）。
/// </summary>
public sealed class MediaJob
{
    private readonly object _gate = new();
    private JobState _state = JobState.Transcoding;
    private double _progress;

    public required string Token { get; init; }
    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public required string SourceDescription { get; init; }
    public required string ArtifactPath { get; init; }
    public required string ContentType { get; init; }

    /// <summary>HLS 产物（m3u8 + 分片目录）：页面走 hls.js / Safari 原生。</summary>
    public bool IsHls { get; init; }
    public required string PlanExplanation { get; init; }

    /// <summary>产物策略变体（转码策略|编码器|格式版本）：任务身份的一部分，
    /// 提交时核对——运行期修改 transcode 配置或源文件被替换 → 不复用旧 job，重新生产。</summary>
    public string? Variant { get; init; }

    /// <summary>源文件是否有视频流（复用核对时算“当前策略变体”用，免重复探测）。</summary>
    public bool HasVideo { get; init; }

    public JobState State { get { lock (_gate) return _state; } }
    public double Progress { get { lock (_gate) return _progress; } }
    public string? Error { get { lock (_gate) return _error; } }
    public IReadOnlyList<SubtitleFile> Subtitles { get { lock (_gate) return _subtitles; } }

    private string? _error;
    private IReadOnlyList<SubtitleFile> _subtitles = [];
    private bool _subsDone;

    /// <summary>进度 / 状态变化通知（控制台进度条等）。</summary>
    public event Action<MediaJob>? Changed;

    /// <summary>字幕是否仍在后台抽取（页面据此决定是否继续轮询字幕列表）。</summary>
    public bool SubtitlesPending { get { lock (_gate) return !_subsDone; } }

    public void UpdateProgress(double fraction)
    {
        lock (_gate) { _progress = Math.Clamp(fraction, 0, 1); }
        Changed?.Invoke(this);
    }

    /// <summary>视频可播（直出/复用命中：立即调用，不等字幕；字幕就绪后单独补发）。
    /// 重置字幕未定案标记：带字幕的 SetServing(subs) 后再次调用等同“重新决定字幕”。</summary>
    public void SetServing()
    {
        lock (_gate) { _state = JobState.Serving; _progress = 1; _subsDone = false; }
        Changed?.Invoke(this);
    }

    /// <summary>一次性置可播 + 字幕就绪（全转码路径产物落位后、测试用）。</summary>
    public void SetServing(IReadOnlyList<SubtitleFile> subtitles)
    {
        lock (_gate) { _state = JobState.Serving; _progress = 1; _subtitles = subtitles; _subsDone = true; }
        Changed?.Invoke(this);
    }

    /// <summary>字幕就绪后补发（空表 = 无字幕/抽取失败，只丢字幕不拖垮视频）。</summary>
    public void SetSubtitles(IReadOnlyList<SubtitleFile> subtitles)
    {
        lock (_gate) { _subtitles = subtitles; _subsDone = true; }
        Changed?.Invoke(this);
    }

    public void SetFailed(string error)
    {
        lock (_gate) { _state = JobState.Failed; _error = error; }
        Changed?.Invoke(this);
    }
}
