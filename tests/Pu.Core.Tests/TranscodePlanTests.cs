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
    public void H264_Mp4_但Moov在尾部_重封装为Hls()
    {
        var plan = TranscodePlan.Create(Media("h264"), Nvenc, isFastStart: false);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
        Assert.Contains("-c:a copy", Args(plan));
        // HLS（m3u8 + TS 分片）：浏览器拿到首个分片即播，不受 moov/Range/渐进解析影响
        Assert.Contains("-hls_time 2", Args(plan));
        Assert.Contains("independent_segments", Args(plan));
        Assert.Equal("mp4.hls", plan.OutputExtension);
        Assert.True(plan.Hls);
    }

    [Fact]
    public void H264_Mkv容器_转封装为Mp4_视频Copy()
    {
        var plan = TranscodePlan.Create(Media("h264", container: "matroska,webm"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
    }

    [Fact]
    public void H264_音频Eac3_音轨直接Copy()
    {
        // Chrome/Edge/Safari 都能解 MP4 里的 ec-3，不再转 AAC（省掉分钟级音频转码）
        var plan = TranscodePlan.Create(Media("h264", audio: "eac3", container: "matroska,webm"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
        Assert.Contains("-c:a copy", Args(plan));
        // HLS 统一；eac3 音轨 copy 保留（封装格式 -f hls 由 Transcoder 统一追加）
        Assert.True(plan.Hls);
    }

    [Fact]
    public void H264_音频Eac3_FastStartMp4_零处理直出()
    {
        var plan = TranscodePlan.Create(Media("h264", audio: "eac3"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.ServeOriginal, plan.Kind);
        Assert.Empty(plan.OutputArgs);
    }

    [Fact]
    public void H264_音频浏览器不能解_音频转Aac_视频Copy()
    {
        var plan = TranscodePlan.Create(Media("h264", audio: "dts"), Nvenc, isFastStart: true);
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
    public void HevcMain10_视频Copy_不打码()
    {
        // iOS 11+ / A9+ 原生支持 HEVC Main10 —— 10bit 也无需重编码（手机 4K 视频的主流格式）
        var plan = TranscodePlan.Create(
            Media("hevc", bitDepth: 10, pixFmt: "yuv420p10le", profile: "Main 10"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
        Assert.Contains("-c:v copy", Args(plan));
        Assert.Contains("-tag:v hvc1", Args(plan));
    }

    [Fact]
    public void Hevc12bit_全转码_优先硬件编码器()
    {
        var plan = TranscodePlan.Create(
            Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
        Assert.Contains("h264_nvenc", Args(plan));
        Assert.Equal("h264_nvenc", plan.EncoderName);
    }

    [Fact]
    public void Hevc12bit_无硬件编码器_回退Libx264()
    {
        var plan = TranscodePlan.Create(
            Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), Soft, isFastStart: true);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
        Assert.Contains("libx264", Args(plan));
        Assert.Contains("-crf 23", Args(plan));
        Assert.Equal("libx264", plan.EncoderName);
    }

    // ── 硬编失败兑底（WithSoftwareEncoder）──

    [Fact]
    public void 硬编失败_替换为Libx264并去掉硬件解码()
    {
        var plan = TranscodePlan.Create(
            Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), Nvenc, isFastStart: true);
        var fallback = plan.WithSoftwareEncoder();

        Assert.Equal(PlanKind.FullTranscode, fallback.Kind);
        Assert.Equal("libx264", fallback.EncoderName);
        Assert.Empty(fallback.EffectiveInputArgs); // -hwaccel 一并去掉
        Assert.Contains("-c:v libx264", Args(fallback));
        Assert.Contains("-crf 23", Args(fallback));
        Assert.DoesNotContain("h264_nvenc", Args(fallback));
        Assert.DoesNotContain("-cq 23", Args(fallback));
        // 与硬编计划其余部分一致（map/像素格式/关键帧/HLS 参数原样保留）
        Assert.Equal("mp4.hls", fallback.OutputExtension);
        Assert.True(fallback.Hls);
        Assert.Contains("-force_key_frames", Args(fallback));
        Assert.Contains("-hls_time 2", Args(fallback));
        Assert.Contains("libx264 软编兜底", fallback.Explanation);
    }

    [Fact]
    public void 硬编失败_Amf块替换正确()
    {
        // AMF 的参数块最长（-quality speed -rc cqp -qp_i 23 -qp_p 23），验证整块替换不残留
        var catalog = new EncoderCatalog(["h264_amf", "libx264"]);
        var plan = TranscodePlan.Create(
            Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), catalog, isFastStart: true);
        var fallback = plan.WithSoftwareEncoder();
        Assert.Contains("-c:v libx264", Args(fallback));
        Assert.DoesNotContain("h264_amf", Args(fallback));
        Assert.DoesNotContain("-qp_i", Args(fallback));
        Assert.DoesNotContain("-qp_p", Args(fallback));
    }

    [Fact]
    public void 硬编失败_软编计划原样返回()
    {
        var plan = TranscodePlan.Create(
            Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), Soft, isFastStart: true);
        Assert.Same(plan, plan.WithSoftwareEncoder());
    }

    [Fact]
    public void 硬编失败_Remux计划原样返回()
    {
        var plan = TranscodePlan.Create(Media("h264"), Nvenc, isFastStart: false); // Remux
        Assert.Null(plan.EncoderName);
        Assert.Same(plan, plan.WithSoftwareEncoder());
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
    public void 纯音频Mp3_浏览器原生可解_直接直出()
    {
        var info = new MediaInfo
        {
            FileName = "song.mp3",
            FormatName = "mp3",
            Streams = [new AudioStreamInfo(0, "mp3", 44100, 2)],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.ServeOriginal, plan.Kind);
    }

    [Fact]
    public void 纯音频FlacWav_浏览器原生可解_直接直出()
    {
        var flac = new MediaInfo
        {
            FileName = "song.flac",
            FormatName = "flac",
            Streams = [new AudioStreamInfo(0, "flac", 44100, 2)],
        };
        Assert.Equal(PlanKind.ServeOriginal, TranscodePlan.Create(flac, Nvenc, isFastStart: true).Kind);

        var wav = new MediaInfo
        {
            FileName = "song.wav",
            FormatName = "wav",
            Streams = [new AudioStreamInfo(0, "pcm_s16le", 44100, 2)],
        };
        Assert.Equal(PlanKind.ServeOriginal, TranscodePlan.Create(wav, Nvenc, isFastStart: true).Kind);
    }

    [Fact]
    public void 纯音频Mp3码流在M4A容器_扩展名不符_不直出转AAC()
    {
        // 扩展名决定 Content-Type：m4a 容器里的 mp3 码流直出会拿到 audio/mp4，
        // 浏览器可能解不了 → 保持原逻辑封装为 M4A 转 AAC
        var info = new MediaInfo
        {
            FileName = "song.m4a",
            FormatName = Mp4,
            Streams = [new AudioStreamInfo(0, "mp3", 44100, 2)],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: false);
        Assert.Equal(PlanKind.Remux, plan.Kind);
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
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), Nvenc, isFastStart: true);
        Assert.Contains("-pix_fmt yuv420p", Args(plan));
    }

    [Fact]
    public void 硬件编码器_带硬件解码输入参数()
    {
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), Nvenc, isFastStart: true);
        Assert.Equal(["-hwaccel", "cuda"], plan.EffectiveInputArgs);
    }

    [Fact]
    public void 软编_无硬件解码参数()
    {
        var plan = TranscodePlan.Create(Media("hevc", bitDepth: 12, pixFmt: "yuv420p12le", profile: "Main 12"), Soft, isFastStart: true);
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
                new VideoStreamInfo(0, "hevc", "Main 12", "yuv420p12le", 128, 72, 12),
                new AudioStreamInfo(1, "aac", 48000, 2),
            ],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Contains("libx264", Args(plan));
        Assert.Empty(plan.EffectiveInputArgs);
    }

    // ── 强制转码策略（ForceGpu）──

    [Fact]
    public void 强制策略_直出文件也全转码()
    {
        // 原本「H.264 + AAC + MP4 + faststart」是零处理直出，强制策略下也必须重编码
        var plan = TranscodePlan.Create(Media("h264"), Nvenc, isFastStart: true, TranscodePolicy.ForceGpu);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
        Assert.Contains("h264_nvenc", Args(plan));
        Assert.Contains("强制转码", plan.Explanation);
        Assert.Equal(["-hwaccel", "cuda"], plan.EffectiveInputArgs);
    }

    [Fact]
    public void 强制策略_无Gpu时回退软编()
    {
        var plan = TranscodePlan.Create(Media("h264"), Soft, isFastStart: true, TranscodePolicy.ForceGpu);
        Assert.Equal(PlanKind.FullTranscode, plan.Kind);
        Assert.Contains("libx264", Args(plan));
        Assert.Empty(plan.EffectiveInputArgs);
    }

    [Fact]
    public void 强制策略_过小视频仍回退软编()
    {
        var info = new MediaInfo
        {
            FileName = "tiny.mp4",
            FormatName = Mp4,
            DurationUs = 1_000_000,
            Streams = [new VideoStreamInfo(0, "h264", "High", "yuv420p", 128, 72, 8)],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true, TranscodePolicy.ForceGpu);
        Assert.Contains("libx264", Args(plan));
        Assert.Empty(plan.EffectiveInputArgs);
    }

    [Fact]
    public void 强制策略_纯音频不受影响()
    {
        var info = new MediaInfo
        {
            FileName = "song.aac",
            FormatName = Mp4,
            DurationUs = 60_000_000,
            Streams = [new AudioStreamInfo(0, "aac", 44100, 2)],
        };
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true, TranscodePolicy.ForceGpu);
        Assert.NotEqual(PlanKind.FullTranscode, plan.Kind);
    }

    [Fact]
    public void 编码器优先级_Amf优先于Qsv_独显优先()
    {
        // 静态优先级 NVENC→AMF→QSV：N 卡必为独显，AMF 多为独显，QSV 基本是核显
        var catalog = new EncoderCatalog(["h264_qsv", "h264_amf", "libx264"]);
        Assert.Equal("h264_amf", catalog.PreferredH264Encoder);
    }

    // ── RequiresEncoder：与决策矩阵分支同步（决定要不要跑编码器探测）──

    [Theory]
    [InlineData("h264", 8, false)]    // 直出/Remux 捷径
    [InlineData("hevc", 8, false)]    // hvc1 Remux
    [InlineData("hevc", 10, false)]   // Main10 → hvc1 Remux
    [InlineData("hevc", 12, true)]    // Main 12 → 全转码
    [InlineData("av1", 8, true)]
    [InlineData("vp9", 8, true)]
    [InlineData("h264", 10, true)]    // Hi10P → 全转码
    public void RequiresEncoder_与矩阵分支一致(string codec, int bitDepth, bool expected)
    {
        var profile = codec == "hevc" && bitDepth == 10 ? "Main 10"
            : codec == "hevc" && bitDepth > 10 ? "Main 12"
            : "High";
        var pixFmt = bitDepth > 8 ? $"yuv420p{bitDepth}le" : "yuv420p";
        var info = Media(codec, bitDepth: bitDepth, pixFmt: pixFmt, profile: profile);
        Assert.Equal(expected, TranscodePlan.RequiresEncoder(info, TranscodePolicy.Auto));
        // 与矩阵结果互相印证：不需要编码器 ⇔ 不是全转码
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Equal(expected, plan.Kind == PlanKind.FullTranscode);
    }

    [Fact]
    public void RequiresEncoder_强制策略下有视频就需要()
    {
        Assert.True(TranscodePlan.RequiresEncoder(Media("h264"), TranscodePolicy.ForceGpu));
    }

    [Fact]
    public void RequiresEncoder_纯音频不需要()
    {
        var info = new MediaInfo
        {
            FileName = "song.aac",
            FormatName = Mp4,
            DurationUs = 60_000_000,
            Streams = [new AudioStreamInfo(0, "aac", 44100, 2)],
        };
        Assert.False(TranscodePlan.RequiresEncoder(info, TranscodePolicy.Auto));
        Assert.False(TranscodePlan.RequiresEncoder(info, TranscodePolicy.ForceGpu));
    }

    [Fact]
    public void HEVC_Main10_Remux不需要编码器()
    {
        var info = Media("hevc", bitDepth: 10, pixFmt: "yuv420p10le", profile: "Main 10");
        Assert.False(TranscodePlan.RequiresEncoder(info, TranscodePolicy.Auto));
        var plan = TranscodePlan.Create(info, Nvenc, isFastStart: true);
        Assert.Equal(PlanKind.Remux, plan.Kind);
    }
}
