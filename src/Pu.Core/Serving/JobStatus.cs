using System.Text.Json.Serialization;

namespace Pu.Core.Serving;

/// <summary>状态页轮询的 JSON（/s/{token}/status）。source-gen 保证 NativeAOT 可用。</summary>
/// <summary>SubsPending：字幕仍在后台抽取（直出/复用命中时视频先可播、字幕后补），
/// 页面据此在就绪态继续慢轮询字幕列表，字幕定案后停止轮询。
/// HasVideo：纯音频文件（mp3/m4a…）页面据此切换为音频播放器，不显示大播放键。</summary>
/// <summary>Folder：从文件夹列表点开时给出所属会话与前后集（上一集/下一集、自动连播用），单文件右键为 null。</summary>
public sealed record JobStatusDto(
    string State, double Progress, string? Error, string Title, string Plan,
    IReadOnlyList<SubtitleDto> Subtitles, bool Hls, bool SubsPending, bool HasVideo,
    FolderLinkDto? Folder = null);

public sealed record SubtitleDto(int Index, string Codec, string Language, string Title, string? Label);

/// <summary>播放页所属的文件夹会话：Position 是当前文件在列表里的序号（从 0 起），Prev/Next 为相邻文件。</summary>
public sealed record FolderLinkDto(string Token, string Title, int Position, int Count, FolderFileDto? Prev, FolderFileDto? Next);

/// <summary>文件夹列表页轮询 JSON（/f/{token}/status）。file.state: new / transcoding / serving / failed；
/// Truncated：扫描因文件数上限被截断（页面显示「500+」并提示）。</summary>
public sealed record FolderStatusDto(string Title, int Count, bool Truncated, IReadOnlyList<FolderFileDto> Files);

/// <summary>Progress：转码中的进度（0–1），其余状态为 0 或 1。</summary>
public sealed record FolderFileDto(int Index, string Name, long SizeBytes, string State, double Progress = 0);

/// <summary>点开文件后返回的状态页 URL（/f/{token}/open/{index}）。</summary>
public sealed record OpenResultDto(string Url);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JobStatusDto))]
[JsonSerializable(typeof(FolderStatusDto))]
[JsonSerializable(typeof(OpenResultDto))]
public sealed partial class JobStatusJsonContext : JsonSerializerContext;
