namespace Pu.Core.Serving;

public static class ContentTypes
{
    public static string ForMedia(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        // QuickTime 容器结构即 ISO BMFF：video/quicktime 在 Chrome/Android 不可靠。
        // 直出矩阵限 h264 + mp4 家族（含 mov），按 video/mp4 声明让 Chromium 正常嗅探播放。
        ".mov" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".ts" or ".mts" or ".m2ts" => "video/mp2t",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".opus" => "audio/ogg",
        ".wma" => "audio/x-ms-wma",
        ".ac3" => "audio/ac3",
        ".dts" => "audio/vnd.dts",
        _ => "application/octet-stream",
    };
}
