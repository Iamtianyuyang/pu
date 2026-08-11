namespace Pu.Core.Serving;

/// <summary>嵌入的播放/状态页（web/index.html，随程序集发布，不依赖外网）。</summary>
public static class EmbeddedWeb
{
    public static string IndexHtml { get; } = Load("web.index.html");
    public static string FolderHtml { get; } = Load("web.folder.html");
    public static string HlsJs { get; } = Load("web.hls.min.js"); // hls.js (Apache-2.0)
    public static byte[] LogoPng { get; } = LoadBytes("assets.pu-logo.png");

    private static string Load(string name)
    {
        using var s = typeof(EmbeddedWeb).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少嵌入的资源 {name}（检查 csproj EmbeddedResource）");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static byte[] LoadBytes(string name)
    {
        using var stream = typeof(EmbeddedWeb).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少嵌入的资源 {name}（检查 csproj EmbeddedResource）");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
