using Pu.Core.Common;
using Pu.Core.Pipeline;
using Pu.Core.Planning;
using Pu.Core.Probe;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>
/// 全格式转换矩阵集成测试：对右键菜单注册的全部 34 个扩展名（+ 关键编码变体）生成真实样本，
/// 走完整链路 生成 → 探测 → 决策矩阵 → 转码 → 产物校验。
/// 每行断言：决策分支符合预期（直出/Remux/全转码）、RequiresEncoder 与矩阵一致、
/// 产物真实可探测（HLS 播放列表引用的分片存在且能解）。
/// 环境缺 ffmpeg 时静默跳过（与其它集成测试一致）。
/// </summary>
public class FormatMatrixTests
{
    /// <summary>编码器目录全局探测一次（含硬编实测，耗时数秒）；仅供全转码分支使用。</summary>
    private static readonly Lazy<Task<EncoderCatalog>> Catalog = new(static () => EncoderCatalog.DetectAsync());

    public static IEnumerable<object[]> Cases => AllCases.Select(c => new object[] { c });

    // ── 覆盖矩阵：34 个注册扩展名 + 关键编码变体（生成参数 / 预期决策分支 / 预期产物编码）──
    private static readonly FormatCase[] AllCases =
    [
        // ── 视频 · 直出（H.264 + MP4 族 + faststart + 可播音轨 → 零处理）──
        new("mp4-h264-faststart 直出", "t.mp4",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-movflags", "+faststart", "-f", "mp4"],
            true, PlanKind.ServeOriginal, Hls: false),

        // ── 视频 · Remux（copy 重封装 HLS）──
        new("mp4-h264 无faststart Remux", "t.mp4",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "mp4"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("mp4-hevc-main10 Remux", "t.mp4",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
             "-x265-params", "log-level=error", "-c:a", "aac", "-movflags", "+faststart", "-f", "mp4"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "hevc"),
        new("m4v-h264 Remux", "t.m4v",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "mp4"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("mov-h264 Remux", "t.mov",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "mov"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("mkv-h264 Remux", "t.mkv",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "matroska"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("mkv-hevc-main10 Remux", "t.mkv",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le",
             "-x265-params", "log-level=error", "-c:a", "aac", "-f", "matroska"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "hevc"),
        new("flv-h264 Remux", "t.flv",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "flv"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("f4v-h264 Remux", "t.f4v",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "f4v"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("ts-h264 Remux", "t.ts",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "mpegts"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("mts-h264 Remux", "t.mts",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "mpegts"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("m2ts-h264 Remux", "t.m2ts",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "mpegts"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("3gp-h264 Remux", "t.3gp",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "3gp"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),
        new("3g2-h264 Remux", "t.3g2",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-f", "3g2"],
            true, PlanKind.Remux, Hls: true, ExpectedOutVideo: "h264"),

        // ── 视频 · 全转码（→ H.264）──
        new("mp4-av1 全转码", "t.mp4",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libaom-av1", "-crf", "40", "-b:v", "0", "-cpu-used", "8",
             "-row-mt", "1", "-c:a", "aac", "-movflags", "+faststart", "-f", "mp4"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("mkv-av1 全转码", "t.mkv",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libaom-av1", "-crf", "40", "-b:v", "0", "-cpu-used", "8",
             "-row-mt", "1", "-c:a", "libopus", "-f", "matroska"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("webm-vp8 全转码", "t.webm",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libvpx", "-deadline", "realtime", "-cpu-used", "8",
             "-c:a", "libvorbis", "-f", "webm"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("webm-vp9 全转码", "t.webm",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8",
             "-c:a", "libopus", "-f", "webm"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("avi-mpeg4 全转码", "t.avi",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "mpeg4", "-c:a", "libmp3lame", "-f", "avi"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("divx-mpeg4 全转码", "t.divx",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "mpeg4", "-c:a", "libmp3lame", "-f", "avi"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("wmv-wmv2 全转码", "t.wmv",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "wmv2", "-c:a", "libmp3lame", "-f", "asf"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("asf-wmv2 全转码", "t.asf",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "wmv2", "-c:a", "libmp3lame", "-f", "asf"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("ts-mpeg2 全转码", "t.ts",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "mpeg2video", "-c:a", "mp2", "-f", "mpegts"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("mpg-mpeg1 全转码", "t.mpg",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "mpeg1video", "-c:a", "mp2", "-f", "mpeg"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("mpeg-mpeg2 全转码", "t.mpeg",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "mpeg2video", "-c:a", "mp2", "-f", "mpeg"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("vob-mpeg2 全转码", "t.vob",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "mpeg2video", "-c:a", "ac3", "-f", "vob"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("m2v-mpeg2 全转码（无音频）", "t.m2v",
            ["-map", "0:v:0", "-c:v", "mpeg2video", "-f", "mpeg2video"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("ogv-theora 全转码", "t.ogv",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "libtheora", "-c:a", "libvorbis", "-f", "ogg"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("rm-rv10 全转码", "t.rm",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "rv10", "-c:a", "ac3", "-f", "rm"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("rmvb-rv10 全转码", "t.rmvb",
            ["-map", "0:v:0", "-map", "1:a:0", "-c:v", "rv10", "-c:a", "ac3", "-f", "rm"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),
        new("hevc-raw 裸码流 全转码", "t.hevc",
            ["-map", "0:v:0", "-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
             "-x265-params", "log-level=error", "-f", "hevc"],
            true, PlanKind.FullTranscode, Hls: true, ExpectedOutVideo: "h264"),

        // ── 音频 · 直出（浏览器原生可解）──
        new("mp3 直出", "t.mp3",
            ["-map", "1:a:0", "-c:a", "libmp3lame", "-f", "mp3"],
            false, PlanKind.ServeOriginal, Hls: false),
        new("flac 直出", "t.flac",
            ["-map", "1:a:0", "-c:a", "flac", "-f", "flac"],
            false, PlanKind.ServeOriginal, Hls: false),
        new("wav 直出", "t.wav",
            ["-map", "1:a:0", "-c:a", "pcm_s16le", "-f", "wav"],
            false, PlanKind.ServeOriginal, Hls: false),
        new("m4a-aac-faststart 直出", "t.m4a",
            ["-map", "1:a:0", "-c:a", "aac", "-movflags", "+faststart", "-f", "mp4"],
            false, PlanKind.ServeOriginal, Hls: false),

        // ── 音频 · Remux（封装 M4A；copy 或转 AAC）──
        new("aac-adts Remux→m4a(copy)", "t.aac",
            ["-map", "1:a:0", "-c:a", "aac", "-f", "adts"],
            false, PlanKind.Remux, Hls: false, ExpectedOutAudio: "aac"),
        new("ac3 Remux→m4a(copy)", "t.ac3",
            ["-map", "1:a:0", "-c:a", "ac3", "-f", "ac3"],
            false, PlanKind.Remux, Hls: false, ExpectedOutAudio: "ac3"),
        new("dts Remux→m4a(转AAC)", "t.dts",
            ["-map", "1:a:0", "-c:a", "dca", "-strict", "-2", "-ar", "48000", "-ac", "2", "-f", "dts"],
            false, PlanKind.Remux, Hls: false, ExpectedOutAudio: "aac"),
        new("opus Remux→m4a(转AAC)", "t.opus",
            ["-map", "1:a:0", "-c:a", "libopus", "-f", "ogg"],
            false, PlanKind.Remux, Hls: false, ExpectedOutAudio: "aac"),
        new("ogg-vorbis Remux→m4a(转AAC)", "t.ogg",
            ["-map", "1:a:0", "-c:a", "libvorbis", "-f", "ogg"],
            false, PlanKind.Remux, Hls: false, ExpectedOutAudio: "aac"),
        new("wma-mp3 Remux→m4a(转AAC)", "t.wma",
            ["-map", "1:a:0", "-c:a", "libmp3lame", "-f", "asf"],
            false, PlanKind.Remux, Hls: false, ExpectedOutAudio: "aac"),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task 全格式转换矩阵(FormatCase c)
    {
        if (!TestEnv.HasFfmpeg) return; // 缺 ffmpeg：静默跳过（仓库约定）
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, c.FileName);
        await MakeFixture(dir.Path, c.FileName, c.GenArgs);

        // 1. 探测
        var info = await MediaProbe.ProbeAsync(src);
        Assert.Equal(c.HasVideo, info.Video is not null);

        // 2. 决策矩阵（与 SessionServer.SubmitCoreAsync 同构：全转码才探测编码器目录）
        Assert.Equal(c.ExpectedKind == PlanKind.FullTranscode,
            TranscodePlan.RequiresEncoder(info, TranscodePolicy.Auto));
        var catalog = c.ExpectedKind == PlanKind.FullTranscode
            ? await Catalog.Value
            : EncoderCatalog.SoftwareOnly;
        var plan = TranscodePlan.Create(info, catalog, src, TranscodePolicy.Auto);
        Assert.Equal(c.ExpectedKind, plan.Kind);
        Assert.Equal(c.Hls, plan.Hls);

        // 直出：产物就是源文件，零处理（决策本身即验证点）
        if (c.ExpectedKind == PlanKind.ServeOriginal)
            return;

        // 3. 转码/重封装（与真实链路同参数：HLS 先建分片目录）
        var outDir = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(outDir);
        var outPath = plan.Hls
            ? Path.Combine(outDir, "index.m3u8")
            : Path.Combine(outDir, $"out.{plan.OutputExtension}");
        await Transcoder.TranscodeAsync(src, plan, outPath, info.DurationUs);

        // 4. 产物校验：HLS 验证播放列表引用真实分片；其余直接探测产物
        if (plan.Hls)
        {
            Assert.True(File.Exists(outPath));
            var m3u8 = File.ReadAllText(outPath);
            Assert.Contains("seg_", m3u8); // 播放列表引用分片
            Assert.Contains(".ts", m3u8);
            var segs = Directory.EnumerateFiles(outDir, "*.ts").ToList();
            Assert.NotEmpty(segs);
            var segInfo = await MediaProbe.ProbeAsync(segs[0]);
            Assert.Equal(c.ExpectedOutVideo, segInfo.Video?.Codec);
        }
        else
        {
            Assert.True(File.Exists(outPath));
            var outInfo = await MediaProbe.ProbeAsync(outPath);
            if (c.ExpectedOutVideo is not null) Assert.Equal(c.ExpectedOutVideo, outInfo.Video?.Codec);
            if (c.ExpectedOutAudio is not null) Assert.Equal(c.ExpectedOutAudio, outInfo.Audio?.Codec);
        }
    }

    /// <summary>生成样本：2 秒 320×240 测试图 + 440Hz 正弦音，按用例参数编码。</summary>
    private static async Task MakeFixture(string dir, string name, string[] codecArgs)
    {
        var args = new List<string>
        {
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
        };
        args.AddRange(codecArgs);
        args.Add(Path.Combine(dir, name));
        var r = await ProcessRunner.RunAsync("ffmpeg", args);
        Assert.True(r.ExitCode == 0, $"生成样本失败: {r.StdErr}");
    }

    public sealed record FormatCase(
        string Name, string FileName, string[] GenArgs, bool HasVideo,
        PlanKind ExpectedKind, bool Hls, string? ExpectedOutVideo = null, string? ExpectedOutAudio = null);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = TestEnv.NewTestDir();
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
