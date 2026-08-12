using Pu.Core.Pipeline;
using Pu.Core.Probe;

namespace Pu.Core.Planning;

public enum PlanKind
{
    Unsupported,     // 无法处理（无视频流等）
    ServeOriginal,   // 零处理直出
    Remux,           // 视频 copy，只动容器/音频/tag
    FullTranscode,   // 必须重编码 → H.264
}

/// <summary>
/// 转码策略：Auto = 决策矩阵「最快看到视频」（能直出就直出、能 copy 就 copy、必须转才转）；
/// ForceGpu = 任何视频一律强制重编码（优先独显硬件编码器）。
/// 由 config.json 的 {"transcode":"auto"|"always"} 控制，默认 auto（见 PuConfig）。
/// </summary>
public enum TranscodePolicy
{
    Auto,
    ForceGpu,
}

/// <summary>
/// 转码决策矩阵（方案.md 第五节）—— 整个工具的核心。
/// InputArgs 是放在 `-i 输入` 之前的参数（如硬件解码 -hwaccel）。
/// OutputArgs 是放在 `-i 输入` 之后、输出路径之前的参数。
/// </summary>
public sealed record TranscodePlan(
    PlanKind Kind, string Explanation, string[] OutputArgs, string OutputExtension, string[]? InputArgs = null)
{
    public string[] EffectiveInputArgs => InputArgs ?? [];

    /// <summary>全转码分支选用的编码器名（h264_nvenc 等）；硬编失败时 WithSoftwareEncoder 重建参数。直出/Remux 为 null。</summary>
    public string? EncoderName { get; init; }

    /// <summary>HLS 产物（m3u8 + 分片目录）：Remux/全转码都走，浏览器拿到首分片即播。</summary>
    public bool Hls { get; init; }

    /// <summary>HLS 产物：m3u8 引用同目录分片，RelativeSegments 占位由 Transcoder 展开。
    /// 封装格式（-f hls）由 Transcoder 统一追加（产物先写 .tmp，扩展名推断会失败）。</summary>
    public static readonly string[] HlsOutputArgs =
    ["-hls_time", "2", "-hls_playlist_type", "vod", "-hls_list_size", "0",
     "-hls_flags", "independent_segments",
     // 长视频多音轨 copy 时 muxing queue 默认 128 包会溢出（“Too many packets buffered for output stream”），
     // 1024 对 2s 分片 + 多轨是安全值；只影响缓冲不影响产物字节
     "-max_muxing_queue_size", "1024"];

    /// <summary>
    /// 产物格式版本：改封装参数（如 movflags）时 +1，调用方把它编进缓存变体，
    /// 让旧参数产出的缓存/就地产物自动失效重转。
    /// v1: Remux 用 empty_moov（Chromium 系 progressive 播放不起，canplay 不触发）
    /// v2: Remux 统一 default_base_moof（moov 完整了，但 Chromium 渐进播放不吃 fMP4：
    ///     整文件顺序下完才播 —— 手机 Wi-Fi 下就是「一直转圈」）
    /// v3: Remux 回到常规 MP4 + faststart（moov 在头部，全浏览器拿到开头就开播）
    /// v4: Remux/全转码产物改为 HLS（m3u8 + TS 分片）——渐进 MP4 在旧版 Chrome 上
    ///     仍整文件下载；HLS 拿到首分片即播，Safari 原生、其余 hls.js
    /// v5: 分片 6s→2s + independent_segments（拖动延迟实测降 ~60%）；转码加 2s 强制关键帧
    /// </summary>
    public const int FormatVersion = 5;

    /// <summary>
    /// 这个文件按矩阵走是否会进入全转码分支（即需要硬件编码器目录）。
    /// 与 Create 的分支条件保持同步；直出/Remux/纯音频返回 false → 调用方可跳过编码器探测。
    /// </summary>
    public static bool RequiresEncoder(MediaInfo info, TranscodePolicy policy)
    {
        var video = info.Video;
        if (video is null) return false;              // 纯音频：只用内置 aac 编码器
        if (policy == TranscodePolicy.ForceGpu) return true;
        bool is8Bit = video.BitDepth <= 8;
        if (video.Codec == "h264" && is8Bit) return false;
        bool hevcMain10 = video.Codec == "hevc" && video.Profile.Contains("Main 10", StringComparison.OrdinalIgnoreCase);
        if (video.Codec == "hevc" && (is8Bit || hevcMain10)) return false;
        return true;
    }

    public static TranscodePlan Create(MediaInfo info, EncoderCatalog encoders, string filePath,
        TranscodePolicy policy = TranscodePolicy.Auto)
        => Create(info, encoders, Mp4Boxes.IsFastStart(filePath), policy);

    public static TranscodePlan Create(MediaInfo info, EncoderCatalog encoders, bool isFastStart,
        TranscodePolicy policy = TranscodePolicy.Auto)
    {
        var video = info.Video;
        if (video is null)
        {
            // 纯音频：浏览器原生可解的格式直出（mp3 全平台；FLAC/WAV Chrome/Edge/Safari/Android 均支持），
            // AAC+MP4+faststart 直出，其余封装为 M4A（浏览器不能解的转 AAC）
            var audio = info.Audio;
            if (audio is null)
                return new TranscodePlan(PlanKind.Unsupported, "文件中没有可用的音视频流", [], "mp4");
            bool isMp4 = info.FormatName.Contains("mp4", StringComparison.OrdinalIgnoreCase);
            if (audio.Codec == "aac" && isMp4 && isFastStart)
                return new TranscodePlan(PlanKind.ServeOriginal, "AAC + MP4 已 faststart，原样直出", [], "m4a");
            // 直出按「扩展名 + 编码/容器」双重门控：Content-Type 由扩展名决定（audio/mpeg 等），
            // 扩展名与编码不一致时（如 m4a 容器里的 mp3 码流）不进直出——否则浏览器拿到
            // 与码流不符的 MIME，反而播不了，保持原逻辑封装为 M4A 转 AAC
            var ext = Path.GetExtension(info.FileName);
            if (audio.Codec == "mp3" && string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
                return new TranscodePlan(PlanKind.ServeOriginal, "MP3 浏览器原生可解，原样直出", [], "mp3");
            if (audio.Codec == "flac" && string.Equals(ext, ".flac", StringComparison.OrdinalIgnoreCase))
                return new TranscodePlan(PlanKind.ServeOriginal, "FLAC 浏览器原生可解，原样直出", [], "flac");
            if (info.FormatName.Contains("wav", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
                return new TranscodePlan(PlanKind.ServeOriginal, "WAV 浏览器原生可解，原样直出", [], "wav");
            bool copyable = audio.Codec is "aac" or "ac3" or "eac3";
            var args = new List<string>
            {
                "-map", "0:a:0",
                "-c:a", copyable ? "copy" : "aac",
                "-movflags", "+faststart",
                "-max_muxing_queue_size", "1024", // 长音频多轨 copy 防 muxing queue 溢出
            };
            return new TranscodePlan(PlanKind.Remux,
                $"音频 {audio.Codec} 封装为 M4A（{(copyable ? "copy" : "转 AAC")}）",
                args.ToArray(), "m4a");
        }

        bool isMp4Family = info.FormatName.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                        || info.FormatName.Contains("mov", StringComparison.OrdinalIgnoreCase);
        // 浏览器可直接解的音轨：AAC 与杜比 AC-3/E-AC-3（Chrome/Edge/Safari 均支持 MP4 里的 ac-3/ec-3）
        bool audioCopyable = info.Audio is null || info.Audio.Codec is "aac" or "ac3" or "eac3";
        bool is8Bit = video.BitDepth <= 8;

        // ── 0. 强制转码策略：跳过一切 copy 捷径，一律重编码（优先 GPU）──
        if (policy == TranscodePolicy.ForceGpu)
            return BuildFullTranscode(info, encoders, video, audioCopyable, forced: true);

        // ── 1. 零处理直出：H.264 8bit + MP4 + faststart + 可播音轨 ──
        if (video.Codec == "h264" && is8Bit && isMp4Family && audioCopyable && isFastStart)
            return new TranscodePlan(PlanKind.ServeOriginal, "H.264 + 可播音轨 + MP4 已 faststart，原样直出，零处理", [], "mp4");

        // ── 2. H.264 8bit：视频 copy，只补容器 / 音频 ──
        if (video.Codec == "h264" && is8Bit)
        {
            var args = BuildMaps(info);
            args.AddRange(["-c:v", "copy"]);
            AddAudio(args, info, audioCopyable);
            args.AddRange(HlsOutputArgs);
            var why = !isMp4Family
                ? "容器非 MP4，转封装为 HLS（视频 copy）"
                : !audioCopyable
                    ? $"音频 {info.Audio!.Codec} 需转 AAC，视频 copy"
                    : "moov 在文件尾部，重封装为 HLS（视频 copy）";
            return new TranscodePlan(PlanKind.Remux, why, args.ToArray(), "mp4.hls") { Hls = true };
        }

        // ── 3. HEVC 8bit / Main10：视频 copy + tag 改 hvc1（Safari/iOS 原生支持，方案.md 第五节）──
        bool hevcMain10 = video.Codec == "hevc" && video.Profile.Contains("Main 10", StringComparison.OrdinalIgnoreCase);
        if (video.Codec == "hevc" && (is8Bit || hevcMain10))
        {
            var args = BuildMaps(info);
            args.AddRange(["-c:v", "copy", "-tag:v", "hvc1"]);
            AddAudio(args, info, audioCopyable);
            args.AddRange(HlsOutputArgs);
            return new TranscodePlan(PlanKind.Remux,
                $"HEVC {video.Profile} 视频 copy，重封装为 HLS（Safari 必需）", args.ToArray(), "mp4.hls") { Hls = true };
        }

        // ── 4. 全转码：HEVC 10bit / Hi10P / AV1 / VP9 / 其它 ──
        return BuildFullTranscode(info, encoders, video, audioCopyable, forced: false);
    }

    private static TranscodePlan BuildFullTranscode(
        MediaInfo info, EncoderCatalog encoders, VideoStreamInfo video, bool audioCopyable, bool forced)
    {
        var enc = encoders.PreferredH264Encoder ?? "libx264";
        if (enc != "libx264" && (video.Width < 256 || video.Height < 144))
            enc = "libx264"; // 过小视频低于硬编最小尺寸（如 NVENC 145px），直接软编避免必然失败的尝试
        var full = BuildMaps(info);
        full.AddRange(["-pix_fmt", "yuv420p"]);
        full.AddRange(EncoderCatalog.ArgsFor(enc));
        full.AddRange(["-tag:v", "avc1"]);
        // 每 2s 一个关键帧：HLS 拖动落到 2s 粒度（copy 路径改不了关键帧，只能靠源 GOP）
        full.AddRange(["-force_key_frames", "expr:gte(t,n_forced*2)"]);
        AddAudio(full, info, audioCopyable);
        full.AddRange(HlsOutputArgs);
        // 硬件编码器配硬件解码（失败时 Transcoder 自动软解回退）
        var hwaccel = EncoderCatalog.HwaccelFor(enc);
        var inputArgs = hwaccel is null ? [] : new[] { "-hwaccel", hwaccel };
        var how = enc == "libx264" ? "libx264 软编" : $"{enc} 硬编";
        var explanation = forced
            ? $"强制转码 {video.Codec} → H.264（{how}）"
            : $"{video.Codec} {video.BitDepth}bit 全转码 → H.264（{how}）";
        return new TranscodePlan(PlanKind.FullTranscode, explanation, full.ToArray(), "mp4.hls", inputArgs)
        {
            Hls = true,
            EncoderName = enc,
        };
    }

    /// <summary>
    /// 硬编失败兜底：把 OutputArgs 里的硬编块（-c:v {EncoderName} + 参数）替换为 libx264，
    /// 去掉 -hwaccel 输入参数，返回新计划。软编/非全转码计划原样返回。
    /// </summary>
    public TranscodePlan WithSoftwareEncoder()
    {
        if (EncoderName is null || EncoderName == "libx264") return this;
        var block = EncoderCatalog.ArgsFor(EncoderName);
        var args = OutputArgs.ToList();
        for (var i = 0; i + block.Length <= args.Count; i++)
        {
            if (args[i] != "-c:v" || !string.Equals(args[i + 1], EncoderName, StringComparison.Ordinal))
                continue;
            args.RemoveRange(i, block.Length);
            args.InsertRange(i, EncoderCatalog.ArgsFor("libx264"));
            return new TranscodePlan(PlanKind.FullTranscode,
                $"{Explanation}（{EncoderName} 硬编失败 → libx264 软编兜底）",
                args.ToArray(), OutputExtension, InputArgs: null)
            {
                Hls = Hls,
                EncoderName = "libx264",
            };
        }
        return this; // 找不到编码器块（理论上不可能）：原样返回
    }

    private static List<string> BuildMaps(MediaInfo info)
    {
        var args = new List<string> { "-map", "0:v:0" };
        if (info.Audio is not null) { args.Add("-map"); args.Add("0:a:0"); }
        return args;
    }

    /// <summary>
    /// Remux/转码产物统一走 HLS（m3u8 + TS 分片）：浏览器拿到首个分片即播，
    /// 不受 moov/Range/渐进解析影响；iPhone Safari 原生支持，其余用 hls.js。
    /// </summary>
    private static void AddAudio(List<string> args, MediaInfo info, bool audioCopyable)
    {
        if (info.Audio is null) return;
        args.Add("-c:a");
        args.Add(audioCopyable ? "copy" : "aac");
    }
}
