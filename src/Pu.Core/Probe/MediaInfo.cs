namespace Pu.Core.Probe;

public abstract record StreamInfo(int Index, string Codec, string CodecType);

public sealed record VideoStreamInfo(
    int Index, string Codec, string Profile, string PixelFormat, int Width, int Height, int BitDepth)
    : StreamInfo(Index, Codec, "video");

public sealed record AudioStreamInfo(
    int Index, string Codec, int SampleRate, int Channels)
    : StreamInfo(Index, Codec, "audio");

public sealed record SubtitleStreamInfo(int Index, string Codec, string Language, string Title)
    : StreamInfo(Index, Codec, "subtitle");

/// <summary>ffprobe 解析结果 —— 转码决策矩阵的唯一输入。</summary>
public sealed class MediaInfo
{
    public required string FileName { get; init; }
    public required string FormatName { get; init; }
    public long DurationUs { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<StreamInfo> Streams { get; init; } = [];

    public VideoStreamInfo? Video => Streams.OfType<VideoStreamInfo>().FirstOrDefault();
    public AudioStreamInfo? Audio => Streams.OfType<AudioStreamInfo>().FirstOrDefault();
    public IReadOnlyList<SubtitleStreamInfo> Subtitles => Streams.OfType<SubtitleStreamInfo>().ToList();
}
