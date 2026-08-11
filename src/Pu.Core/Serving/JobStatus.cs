using System.Text.Json.Serialization;

namespace Pu.Core.Serving;

/// <summary>状态页轮询的 JSON（/s/{token}/status）。source-gen 保证 NativeAOT 可用。</summary>
public sealed record JobStatusDto(
    string State, double Progress, string? Error, string Title, string Plan, string Source,
    IReadOnlyList<SubtitleDto> Subtitles);

public sealed record SubtitleDto(int Index, string Codec, string Language, string Title, string? Label);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JobStatusDto))]
public sealed partial class JobStatusJsonContext : JsonSerializerContext;
