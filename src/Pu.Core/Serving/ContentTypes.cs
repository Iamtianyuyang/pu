namespace Pu.Core.Serving;

public static class ContentTypes
{
    public static string ForMedia(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".ts" or ".mts" or ".m2ts" => "video/mp2t",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".aac" => "audio/aac",
        _ => "application/octet-stream",
    };
}
