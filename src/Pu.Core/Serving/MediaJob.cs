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
    public required string PlanExplanation { get; init; }

    public JobState State { get { lock (_gate) return _state; } }
    public double Progress { get { lock (_gate) return _progress; } }
    public string? Error { get; private set; }
    public IReadOnlyList<SubtitleFile> Subtitles { get; private set; } = [];

    /// <summary>进度 / 状态变化通知（控制台进度条等）。</summary>
    public event Action<MediaJob>? Changed;

    public void UpdateProgress(double fraction)
    {
        lock (_gate) { _progress = Math.Clamp(fraction, 0, 1); }
        Changed?.Invoke(this);
    }

    public void SetServing(IReadOnlyList<SubtitleFile> subtitles)
    {
        lock (_gate) { _state = JobState.Serving; _progress = 1; Subtitles = subtitles; }
        Changed?.Invoke(this);
    }

    public void SetFailed(string error)
    {
        lock (_gate) { _state = JobState.Failed; Error = error; }
        Changed?.Invoke(this);
    }
}
