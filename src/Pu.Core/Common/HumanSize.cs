namespace Pu.Core.Common;

/// <summary>人类可读的文件大小（B / KB / MB / GB）。各 UI 层共用，避免重复实现。</summary>
public static class HumanSize
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (1L << 10)} KB",
        _ => $"{bytes} B",
    };
}
