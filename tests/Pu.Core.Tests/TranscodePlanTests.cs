using Pu.Core.Pipeline;
using Pu.Core.Planning;
using Pu.Core.Probe;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>转码决策矩阵单元测试 —— 纯逻辑，不依赖 ffmpeg。</summary>
public class TranscodePlanTests
{
    private static readonly EncoderCatalog Nvenc = new(["h264_nvenc", "libx264"]);
    private static readonly EncoderCatalog Soft = new(["libx264"]);

    private const string Mp4 = "mov,mp4,m4a,3gp,3g2,mj2";

    private static MediaInfo Media(
        string codec, int bitDepth = 8, string container = Mp4,
        string? audio = "aac", string pixFmt = "yuv420p", string profile = "High")
    {
        var streams = new List<StreamInfo> { new VideoStreamInfo(0, codec, profile, pixFmt, 1920, 1080, bitDepth) };
        if (audio is not null) streams.Add(new AudioStreamInfo(1, audio, 48000, 2));
        return new MediaInfo
        {
            FileName = "movie.mkv",
            FormatName = container,
            DurationUs = 60_000_000,
            Streams = streams,
        };
    }

    private static string Args(TranscodePlan plan) => string.Join(' ', plan.OutputArgs);

    // ── 矩阵四行 ──

    [Fact]
    public void H264_Aac_Mp4_FastStart_直出零处理()
    {
        var plan = TranscodePlan.Create(Media("h264"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.ServeOriginal, plan.Kind);
        Assert.Empty(plan.OutputArgs);
    }

    [Fact]
    public void H264_Mp4_但Moov在尾部_转封装重排FastStart()
    {
        var plan = TranscodePlan.Create(Media("h264"), Nvenc, isFastStart: false);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
        Assert.Contains("-c:a copy", Args(plan));
        Assert.Contains("-movflags +faststart", Args(plan));
    }

    [Fact]
    public void H264_Mkv容器_转封装为Mp4_视频Copy()
    {
        var plan = TranscodePlan.Create(Media("h264", container: "matroska,webm"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
    }

    [Fact]
    public void H264_音频非Aac_音频转Aac_视频Copy()
    {
        var plan = TranscodePlan.Create(Media("h264", audio: "ac3"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
        Assert.Contains("-c:a aac", Args(plan));
    }

    [Fact]
    public void Hevc8bit_视频Copy_必须打Hvc1Tag()
    {
        var plan = TranscodePlan.Create(Media("hevc", profile: "Main"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
        Assert.Contains("-tag:v hvc1", Args(plan)); // Safari 黑屏不报错的坑
    }

    [Fact]
    public void Hevc10bit_全转码_优先硬件编码器()
    {
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 10, pixFmt: "yuv420p10le", profile: "Main 10"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
        Assert.Contains("h264_nvenc", Args(plan));
    }

    [Fact]
    public void Hevc10bit_无硬件编码器_回退Libx264()
    {
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 10, pixFmt: "yuv420p10le", profile: "Main 10"), Soft, isFastStart: true);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
        Assert.Contains("libx264", Args(plan));
        Assert.Contains("-crf 23", Args(plan));
    }

    [Theory]
    [InlineData("av1")]
    [InlineData("vp9")]
    public void Av1_Vp9_全转码(string codec)
    {
        var plan = TranscodePlan.Create(Media(codec), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
    }

    // ── 边界 ──

    [Fact]
    public void 纯音频AacMp4FastStart_直出()
    {
        var info = new MediaInfo
        {
            FileName = "song.m4a",
            FormatName = Mp4,
            Streams = [new AudioStreamInfo(0, "aac", 44100, 2)],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.ServeOriginal, plan.Kind);
    }

    [Fact]
    public void 纯音频Mp3_封装为M4A_音频转AAC()
    {
        var info = new MediaInfo
        {
            FileName = "song.mp3",
            FormatName = "mp3",
            Streams = [new AudioStreamInfo(0, "mp3", 44100, 2)],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Equal("m4a", plan.OutputExtension);
        Assert.Contains("-map 0:a:0", Args(plan));
        Assert.Contains("-c:a aac", Args(plan));
    }

    [Fact]
    public void 空文件无任何流_不处理()
    {
        var info = new MediaInfo { FileName = "empty.bin", FormatName = "unknown", Streams = [] };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Unsupported, plan.Kind);
    }

    [Fact]
    public void 无音频流_不加音频Map()
    {
        var plan = TranscodePlan.Create(Media("h264", audio: null, container: "matroska,webm"), Nvenc, isFastStart: true);
        Assert.DoesNotContain("0:a:0", Args(plan));
    }

    [Fact]
    public void 全转码_输出像素格式固定Yuv420p()
    {
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 10, pixFmt: "yuv420p10le", profile: "Main 10"), Nvenc, isFastStart: true);
        Assert.Contains("-pix_fmt yuv420p", Args(plan));
    }

    [Fact]
    public void 硬件编码器_带硬件解码输入参数()
    {
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 10, pixFmt: "yuv420p10le", profile: "Main 10"), Nvenc, isFastStart: true);
        Assert.Equal(["-hwaccel", "cuda"], plan.EffectiveInputArgs);
    }

    [Fact]
    public void 软编_无硬件解码参数()
    {
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 10, pixFmt: "yuv420p10le", profile: "Main 10"), Soft, isFastStart: true);
        Assert.Empty(plan.EffectiveInputArgs);
    }

    [Fact]
    public void 过小视频_跳过硬件编码直接软编()
    {
        var info = new MediaInfo
        {
            FileName = "tiny.mkv",
            FormatName = "matroska,webm",
            DurationUs = 1_000_000,
            Streams =
            [
                new VideoStreamInfo(0, "hevc", "Main 10", "yuv420p10le", 128, 72, 10),
                new AudioStreamInfo(1, "aac", 48000, 2),
            ],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Contains("libx264", Args(plan));
        Assert.Empty(plan.EffectiveInputArgs);
    }
}
