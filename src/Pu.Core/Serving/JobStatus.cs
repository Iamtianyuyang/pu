using System.Text.Json.Serialization;

namespace Pu.Core.Serving;

/// <summary>状态页轮询的 JSON（/s/{token}/status）。source-gen 保证 NativeAOT 可用。</summary>
public sealed record JobStatusDto(
    string State, double Progress, string? Error, string Title, string Plan, string Source,
    IReadOnlyList<SubtitleDto> Subtitles);

public sealed record SubtitleDto(int Index, string Codec, string Language, string Title, string? Label);

/// <summary>文件夹列表页轮询 JSON（/f/{token}/status）。file.state: new / transcoding / serving / failed。</summary>
public sealed record FolderStatusDto(string Title, int Count, IReadOnlyList<FolderFileDto> Files);

public sealed record FolderFileDto(int Index, string Name, long SizeBytes, string State);

/// <summary>点开文件后返回的状态页 URL（/f/{token}/open/{index}）。</summary>
public sealed record OpenResultDto(string Url);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JobStatusDto))]
[JsonSerializable(typeof(FolderStatusDto))]
[JsonSerializable(typeof(OpenResultDto))]
public sealed partial class JobStatusJsonContext : JsonSerializerContext;
