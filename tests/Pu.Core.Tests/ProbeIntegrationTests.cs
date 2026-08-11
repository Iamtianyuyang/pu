using Pu.Core.Common;
using Pu.Core.Pipeline;
using Pu.Core.Planning;
using Pu.Core.Probe;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>
/// 端到端集成测试：真实 ffmpeg/ffprobe 生成样本 → 探测 → 决策。
/// 环境缺 ffmpeg 时静默跳过（本机已装 D:\Application\ffmpeg\bin）。
/// </summary>
public class ProbeIntegrationTests
{
    [Fact]
    public async Task H264AacMp4FastStart_探测直出()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var path = await MakeVideo(dir.Path, "h264.mp4",
            ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart"]);

        var info = await MediaProbe.ProbeAsync(path);
        Assert.Equal("h264", info.Video?.Codec);
        Assert.Equal("aac", info.Audio?.Codec);
        Assert.True(Mp4Boxes.IsFastStart(path));

        var plan = TranscodePlan.Create(info, new EncoderCatalog(["libx264"]), path);
        Assert.Equal(PlanKind.ServeOriginal, plan.Kind);
    }

    [Fact]
    public async Task H264AacMp4_无FastStart_重排Moov()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var path = await MakeVideo(dir.Path, "slowstart.mp4",
            ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]); // 不加 faststart

        var info = await MediaProbe.ProbeAsync(path);
        var plan = TranscodePlan.Create(info, new EncoderCatalog(["libx264"]), path);
        Assert.Equal(PlanKind.Remux, plan.Kind);
    }

    [Fact]
    public async Task H264Aac_Mkv_转封装()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var path = await MakeVideo(dir.Path, "movie.mkv",
            ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var info = await MediaProbe.ProbeAsync(path);
        Assert.Contains("matroska", info.FormatName);
        var plan = TranscodePlan.Create(info, new EncoderCatalog(["libx264"]), path);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", string.Join(' ', plan.OutputArgs));
    }

    [Fact]
    public async Task Hevc8bit_打Hvc1Tag()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var path = await MakeVideo(dir.Path, "hevc.mp4",
            ["-c:v", "libx265", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart"]);

        var info = await MediaProbe.ProbeAsync(path);
        Assert.Equal("hevc", info.Video?.Codec);
        var plan = TranscodePlan.Create(info, new EncoderCatalog(["libx264"]), path);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-tag:v hvc1", string.Join(' ', plan.OutputArgs));
    }

    [Fact]
    public async Task HevcMain10_打Hvc1Tag_不转码()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var path = await MakeVideo(dir.Path, "hevc10.mp4",
            ["-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-c:a", "aac", "-movflags", "+faststart"]);

        var info = await MediaProbe.ProbeAsync(path);
        Assert.Equal("hevc", info.Video?.Codec);
        Assert.Contains("Main 10", info.Video?.Profile);
        var plan = TranscodePlan.Create(info, new EncoderCatalog(["libx264"]), path);
        Assert.Equal(PlanKind.Remux, plan.Kind); // iOS 11+ 原生支持 Main10 → 直接 copy
        Assert.Contains("-c:v copy", string.Join(' ', plan.OutputArgs));
        Assert.Contains("-tag:v hvc1", string.Join(' ', plan.OutputArgs));
    }

    [Fact]
    public async Task Hevc12bit_全转码()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var path = await MakeVideo(dir.Path, "hevc12.mp4",
            ["-c:v", "libx265", "-pix_fmt", "yuv420p12le", "-c:a", "aac", "-movflags", "+faststart"]);

        var info = await MediaProbe.ProbeAsync(path);
        Assert.Equal(12, info.Video?.BitDepth);
        var plan = TranscodePlan.Create(info, new EncoderCatalog(["libx264"]), path);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
    }

    [Fact]
    public async Task 转码产物_能正常出文件()
    {
        if (!TestEnv.HasFfmpeg) return;
        using var dir = new TempDir();
        var src = await MakeVideo(dir.Path, "src.mkv",
            ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var info = await MediaProbe.ProbeAsync(src);
        var plan = TranscodePlan.Create(info, new EncoderCatalog(["libx264"]), src);
        var outPath = Path.Combine(dir.Path, plan.Hls ? "out.mp4.hls" : $"out.{plan.OutputExtension}");
        if (plan.Hls) Directory.CreateDirectory(outPath);
        await Transcoder.TranscodeAsync(src, plan, plan.Hls ? Path.Combine(outPath, "index.m3u8") : outPath, info.DurationUs);

        Assert.True(File.Exists(plan.Hls ? Path.Combine(outPath, "index.m3u8") : outPath));
        Assert.True(plan.Hls
            ? Directory.EnumerateFiles(outPath).Count(f => f.EndsWith(".ts")) > 0
            : new FileInfo(outPath).Length > 0);

        // 产物重新探测：应是 H.264（HLS 就探测首个分片）
        var outInfo = await MediaProbe.ProbeAsync(plan.Hls
            ? Directory.EnumerateFiles(outPath, "*.ts").First()
            : outPath);
        Assert.Equal("h264", outInfo.Video?.Codec);
        if (!plan.Hls) Assert.True(Mp4Boxes.IsFastStart(outPath));
    }

    private static async Task<string> MakeVideo(string dir, string name, string[] codecArgs)
    {
        var path = Path.Combine(dir, name);
        var args = new List<string>
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=128x72:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
        };
        args.AddRange(codecArgs);
        args.Add(path);
        var r = await ProcessRunner.RunAsync("ffmpeg", args);
        Assert.True(r.ExitCode == 0, $"ffmpeg 生成样本失败: {r.StdErr}");
        return path;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
