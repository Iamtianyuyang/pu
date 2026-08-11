namespace Pu.Core;

/// <summary>媒体扩展名清单（方案.md 第七节）。Pu.App 的右键注册与文件夹扫描共用。</summary>
public static class MediaExtensions
{
    public static readonly string[] Defaults =
    [
        // 视频
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".vob", ".3gp", ".3g2",
        ".ogv", ".rm", ".rmvb", ".asf", ".f4v", ".divx", ".hevc", ".m2v",
        // 音频
        ".mp3", ".aac", ".m4a", ".flac", ".wav", ".ogg", ".opus", ".wma", ".ac3", ".dts",
    ];
}
