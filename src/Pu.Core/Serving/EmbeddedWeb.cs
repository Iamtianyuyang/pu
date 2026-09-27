using System.Security.Cryptography;

namespace Pu.Core.Serving;

/// <summary>嵌入的播放/状态页（web/index.html，随程序集发布，不依赖外网）。</summary>
public static class EmbeddedWeb
{
    public static string IndexHtml { get; } = Load("web.index.html");
    public static string FolderHtml { get; } = Load("web.folder.html");
    public static string HlsJs { get; } = Load("web.hls.min.js"); // hls.js (Apache-2.0)
    public static byte[] LogoPng { get; } = LoadBytes("assets.pu-logo.png");

    /// <summary>夸夸词与噗噗台词（网页气泡和 WPF 共用一份，改词只改 web/words.json）。</summary>
    public static string WordsJson { get; } = Load("web.words.json");

    /// <summary>/assets/{name} 白名单：只有这里列出的名字能被请求到，不存在按名字读任意资源的路径。
    /// Immutable = 第三方或几乎不变的文件，长缓存；其余是页面自己的样式/脚本/吉祥物，
    /// 随版本升级会变，走 ETag 协商（局域网内 304 很便宜，升级后不会拿到旧样式）。</summary>
    public static IReadOnlyDictionary<string, Asset> Assets { get; } = new Dictionary<string, Asset>(StringComparer.Ordinal)
    {
        ["pu-logo.png"] = new(LogoPng, "image/png", Immutable: true),
        ["hls.min.js"] = Asset.Text(HlsJs, "application/javascript; charset=utf-8", immutable: true),
        ["pu.css"] = Asset.Text(Load("web.pu.css"), "text/css; charset=utf-8"),
        ["pu.js"] = Asset.Text(Load("web.pu.js"), "application/javascript; charset=utf-8"),
        ["mascot.svg"] = Asset.Text(Load("web.mascot.svg"), "image/svg+xml; charset=utf-8"),
        ["words.json"] = Asset.Text(WordsJson, "application/json; charset=utf-8"),
    };

    public sealed record Asset(byte[] Bytes, string ContentType, bool Immutable = false)
    {
        /// <summary>内容哈希 ETag（强校验）：同一版本的资源不变，页面刷新走 304。</summary>
        public string ETag { get; } = $"\"{Convert.ToHexString(SHA256.HashData(Bytes), 0, 8)}\"";

        public static Asset Text(string text, string contentType, bool immutable = false)
            => new(System.Text.Encoding.UTF8.GetBytes(text), contentType, immutable);
    }

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
