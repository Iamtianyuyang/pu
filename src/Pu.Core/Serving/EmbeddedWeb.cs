namespace Pu.Core.Serving;

/// <summary>嵌入的播放/状态页（web/index.html，随程序集发布，不依赖外网）。</summary>
public static class EmbeddedWeb
{
    public static string IndexHtml { get; } = Load();

    private static string Load()
    {
        using var s = typeof(EmbeddedWeb).Assembly.GetManifestResourceStream("web.index.html")
            ?? throw new InvalidOperationException("缺少嵌入的播放页 web/index.html（检查 csproj EmbeddedResource）");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
