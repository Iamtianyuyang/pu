using System.Globalization;
using System.Text.Json;
using Pu.Core.Common;

namespace Pu.Core.Probe;

public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(FfmpegLocator.ProbeExe, new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            filePath,
        }, cancellationToken: ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 失败: {result.StdErr.Trim()}");
        return Parse(result.StdOut, filePath);
    }

    public static MediaInfo Parse(string json, string filePath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string formatName = "";
        long durationUs = 0;
        long sizeBytes = 0;
        if (root.TryGetProperty("format", out var format))
        {
            formatName = GetString(format, "format_name") ?? "";
            var duration = GetString(format, "duration");
            if (duration is not null
                && double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d > 0)
                durationUs = (long)(d * 1_000_000);
            var sizeStr = GetString(format, "size");
            if (sizeStr is not null && long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                sizeBytes = s;
        }

        var streams = new List<StreamInfo>();
        if (root.TryGetProperty("streams", out var streamsEl))
        {
            foreach (var s in streamsEl.EnumerateArray())
            {
                var type = GetString(s, "codec_type") ?? "";
                var codec = GetString(s, "codec_name") ?? "";
                var index = s.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : streams.Count;
                switch (type)
                {
                    case "video":
                    {
                        var pixFmt = GetString(s, "pix_fmt");
                        var profile = GetString(s, "profile");
                        streams.Add(new VideoStreamInfo(
                            index, codec, profile ?? "", pixFmt ?? "",
                            GetInt(s, "width"), GetInt(s, "height"),
                            InferBitDepth(pixFmt, profile)));
                        break;
                    }
                    case "audio":
                        streams.Add(new AudioStreamInfo(index, codec, GetInt(s, "sample_rate"), GetInt(s, "channels")));
                        break;
                    case "subtitle":
                        streams.Add(new SubtitleStreamInfo(index, codec, GetTag(s, "language") ?? "", GetTag(s, "title") ?? ""));
                        break;
                }
            }
        }

        return new MediaInfo
        {
            FileName = filePath,
            FormatName = formatName,
            DurationUs = durationUs,
            SizeBytes = sizeBytes,
            Streams = streams,
        };
    }

    /// <summary>位深推断：pix_fmt / profile 里的 10/12-bit 标记。nv12 等 8bit 格式不含这些标记。</summary>
    private static int InferBitDepth(string? pixFmt, string? profile)
    {
        if (pixFmt is not null && (pixFmt.Contains("10le", StringComparison.Ordinal)
            || pixFmt.Contains("10be", StringComparison.Ordinal)
            || pixFmt.Contains("p010", StringComparison.OrdinalIgnoreCase)
            || pixFmt.Contains("12le", StringComparison.Ordinal)
            || pixFmt.Contains("12be", StringComparison.Ordinal)))
            return 10;
        if (profile is not null && profile.Contains("10", StringComparison.Ordinal))
            return 10;
        return 8;
    }

    private static string? GetTag(JsonElement e, string name)
    {
        if (!e.TryGetProperty("tags", out var tags)) return null;
        return GetString(tags, name);
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        // ffprobe 的 sample_rate 等字段是字符串
        if (v.ValueKind == JsonValueKind.String
            && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        return 0;
    }
}
