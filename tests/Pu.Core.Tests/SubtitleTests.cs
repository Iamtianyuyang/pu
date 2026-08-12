using Pu.Core.Common;
using Pu.Core.Pipeline;
using Pu.Core.Probe;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>字幕抽取集成测试：真实 ffmpeg 生成带内嵌字幕的 mkv → 抽成 WebVTT。</summary>
public class SubtitleTests
{
    [Fact]
    public async Task Vtt已存在_跳过抽取直接复用()
    {
        // 无需 ffmpeg：所有 VTT 已存在且 .meta 与源文件一致时不应启动任何进程
        using var dir = new TempDir();
        var sourcePath = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        var subsDir = Path.Combine(dir.Path, "subs");
        Directory.CreateDirectory(subsDir);
        File.WriteAllText(Path.Combine(subsDir, "3.vtt"), "WEBVTT");
        var fi = new FileInfo(sourcePath);
        File.WriteAllText(Path.Combine(subsDir, ".meta"),
            $"{{\"size\":{fi.Length},\"mtime\":{fi.LastWriteTimeUtc.Ticks}}}");

        var info = new MediaInfo
        {
            FileName = sourcePath,
            FormatName = "matroska",
            Streams = [new SubtitleStreamInfo(3, "subrip", "chi", "")],
        };
        var subs = await SubtitleExtractor.ExtractAsync(sourcePath, info, dir.Path);

        var sub = Assert.Single(subs);
        Assert.Equal(3, sub.StreamIndex);
        Assert.Equal(Path.Combine(subsDir, "3.vtt"), sub.VttPath);
        Assert.Equal("WEBVTT", await File.ReadAllTextAsync(sub.VttPath));
    }

    [Fact]
    public async Task 源文件被替换_旧字幕不复用()
    {
        if (!TestEnv.HasFfmpeg) return; // 失配后要真跑 ffmpeg 重新抽取
        using var dir = new TempDir();
        var sourcePath = Path.Combine(dir.Path, "movie.mkv");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        var subsDir = Path.Combine(dir.Path, "subs");
        Directory.CreateDirectory(subsDir);
        File.WriteAllText(Path.Combine(subsDir, "3.vtt"), "WEBVTT: 旧字幕");
        var fi = new FileInfo(sourcePath);
        File.WriteAllText(Path.Combine(subsDir, ".meta"),
            $"{{\"size\":{fi.Length},\"mtime\":{fi.LastWriteTimeUtc.Ticks}}}");

        // 源文件被替换（mtime 变化）→ .meta 失配 → 旧 VTT 不得复用（重新抽取失败 → 空）
        File.WriteAllText(sourcePath, "a completely different file");
        var info = new MediaInfo
        {
            FileName = sourcePath,
            FormatName = "matroska",
            Streams = [new SubtitleStreamInfo(3, "subrip", "chi", "")],
        };
        var subs = await SubtitleExtractor.ExtractAsync(sourcePath, info, dir.Path);

        Assert.Empty(subs); // 旧字幕没有挂到新视频上
    }

    [Fact]
    public async Task 按源隔离的字幕目录_各自复用互不串扰()
    {
        // 模拟 SessionServer 按源指纹隔离的调用：A 的抽取不得触碰 B 的字幕
        using var dir = new TempDir();
        var srcA = Path.Combine(dir.Path, "a.mkv");
        var srcB = Path.Combine(dir.Path, "b.mkv");
        File.WriteAllBytes(srcA, [1, 2, 3, 4]);
        File.WriteAllBytes(srcB, [5, 6, 7, 8]);
        var subsA = Path.Combine(dir.Path, "subs", "keyA");
        var subsB = Path.Combine(dir.Path, "subs", "keyB");
        foreach (var (src, subDir) in new[] { (srcA, subsA), (srcB, subsB) })
        {
            Directory.CreateDirectory(Path.Combine(subDir, "subs"));
            File.WriteAllText(Path.Combine(subDir, "subs", "3.vtt"), "WEBVTT: 各自字幕");
            var fi = new FileInfo(src);
            File.WriteAllText(Path.Combine(subDir, "subs", ".meta"),
                $"{{\"size\":{fi.Length},\"mtime\":{fi.LastWriteTimeUtc.Ticks}}}");
        }

        var info = new MediaInfo
        {
            FileName = srcA,
            FormatName = "matroska",
            Streams = [new SubtitleStreamInfo(3, "subrip", "chi", "")],
        };
        var subs = await SubtitleExtractor.ExtractAsync(srcA, info, subsA);

        var sub = Assert.Single(subs);
        Assert.Equal(Path.Combine(subsA, "subs", "3.vtt"), sub.VttPath);
        Assert.NotEqual(Path.Combine(subsB, "subs", "3.vtt"), sub.VttPath);
        // B 的同流序号字幕原样保留，没被 A 的抽取覆盖
        Assert.Equal("WEBVTT: 各自字幕", await File.ReadAllTextAsync(Path.Combine(subsB, "subs", "3.vtt")));
    }

    [Fact]
    public async Task Mkv内嵌Srt_抽成WebVTT()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();

        // 1. 生成基础视频
        var baseVideo = Path.Combine(dir.Path, "base.mp4");
        var make = await ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=128x72:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
            baseVideo,
        });
        Assert.True(make.ExitCode == 0, $"生成基础视频失败: {make.StdErr}");

        // 2. 内嵌 SRT 字幕
        var srt = Path.Combine(dir.Path, "sub.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,000\n你好世界\n");
        var mkv = Path.Combine(dir.Path, "subbed.mkv");
        var mux = await ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-i", baseVideo, "-i", srt,
            "-map", "0", "-map", "1",
            "-c", "copy", "-c:s", "srt",
            mkv,
        });
        Assert.True(mux.ExitCode == 0, $"封装字幕失败: {mux.StdErr}");

        // 3. 探测应识别字幕流
        var info = await MediaProbe.ProbeAsync(mkv);
        Assert.Single(info.Subtitles);
        Assert.Equal("subrip", info.Subtitles[0].Codec);

        // 4. 抽取 → WebVTT
        var subs = await SubtitleExtractor.ExtractAsync(mkv, info, dir.Path);
        Assert.Single(subs);
        Assert.True(File.Exists(subs[0].VttPath));
        var text = await File.ReadAllTextAsync(subs[0].VttPath);
        Assert.StartsWith("WEBVTT", text);
        Assert.Contains("你好世界", text);
    }

    [Fact]
    public async Task 多条字幕_单遍全抽出()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();

        var baseVideo = Path.Combine(dir.Path, "base.mp4");
        var make = await ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=128x72:rate=10",
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            baseVideo,
        });
        Assert.True(make.ExitCode == 0, $"生成基础视频失败: {make.StdErr}");

        var srt1 = Path.Combine(dir.Path, "a.srt");
        var srt2 = Path.Combine(dir.Path, "b.srt");
        File.WriteAllText(srt1, "1\n00:00:00,000 --> 00:00:01,000\n第一条\n");
        File.WriteAllText(srt2, "1\n00:00:00,000 --> 00:00:01,000\n第二条\n");
        var mkv = Path.Combine(dir.Path, "multi.mkv");
        var mux = await ProcessRunner.RunAsync("ffmpeg", new[]
        {
            "-y", "-v", "error",
            "-i", baseVideo, "-i", srt1, "-i", srt2,
            "-map", "0", "-map", "1", "-map", "2",
            "-c", "copy", "-c:s", "srt",
            mkv,
        });
        Assert.True(mux.ExitCode == 0, $"封装字幕失败: {mux.StdErr}");

        var info = await MediaProbe.ProbeAsync(mkv);
        Assert.Equal(2, info.Subtitles.Count);

        var subs = await SubtitleExtractor.ExtractAsync(mkv, info, dir.Path);
        Assert.Equal(2, subs.Count);
        foreach (var sub in subs)
        {
            var text = await File.ReadAllTextAsync(sub.VttPath);
            Assert.StartsWith("WEBVTT", text);
        }
        var all = subs.Select(s => File.ReadAllText(s.VttPath)).ToList();
        Assert.Contains(all, t => t.Contains("第一条"));
        Assert.Contains(all, t => t.Contains("第二条"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
