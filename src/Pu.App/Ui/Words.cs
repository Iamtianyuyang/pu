using System.Text.Json;
using Pu.Core.Serving;

namespace Pu.App.Ui;

/// <summary>噗噗的台词与夸夸词：读 Pu.Core 嵌入的 web/words.json，和网页共用一份（改词只改那一个文件）。
/// 解析失败退回内置的一两句，气泡不至于空着。</summary>
public static class Words
{
    public static IReadOnlyList<string> Praise { get; }
    public static IReadOnlyList<string> Busy { get; }
    public static IReadOnlyList<string> Delivered { get; }
    public static IReadOnlyList<string> Idle { get; }

    static Words()
    {
        try
        {
            using var doc = JsonDocument.Parse(EmbeddedWeb.WordsJson);
            var root = doc.RootElement;
            var template = root.TryGetProperty("praiseTemplate", out var t) ? t.GetString() ?? "{0}" : "{0}";
            Praise = List(root, "praise").Select(p => template.Replace("{0}", p, StringComparison.Ordinal)).ToList();
            Busy = List(root, "busy");
            Delivered = List(root, "delivered");
            Idle = List(root, "idle");
        }
        catch
        {
            Praise = ["全世界最可爱的噗噗大王~"];
            Busy = ["嘿咻嘿咻，使劲中"];
            Delivered = ["噗~送到啦"];
            Idle = ["右键一个视频试试"];
        }
    }

    private static List<string> List(JsonElement root, string name)
        => root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : [];
}
