using Pu.Core.Common;
using Pu.Core.Pipeline;
using Pu.Core.Probe;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>字幕抽取集成测试：真实 ffmpeg 生成带内嵌字幕的 mkv → 抽成 WebVTT。</summary>
public class SubtitleTests
{
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
