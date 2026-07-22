namespace Recorder.Encoding;

public enum VideoCodec
{
    H264,
    Hevc,
}

public enum RateControlMode
{
    /// <summary>Encoder default (typically CBR at the target bitrate).</summary>
    Default,
    Cbr,
    /// <summary>Peak-constrained VBR around the target bitrate.</summary>
    Vbr,
    /// <summary>Constant quality; <see cref="VideoEncodingConfig.QualityLevel"/> applies, bitrate becomes advisory.</summary>
    ConstantQuality,
}

/// <summary>
/// Resolved video encoding parameters for one recording (no "auto" values left).
/// Width/Height are the OUTPUT dimensions; when they differ from
/// SourceWidth/SourceHeight (capture size), the sink writer's GPU video processor
/// scales during the existing color-convert pass — no extra cost.
/// </summary>
public sealed record VideoEncodingConfig(
    int Width,
    int Height,
    int FramesPerSecond,
    int BitrateBitsPerSecond,
    VideoCodec Codec = VideoCodec.H264,
    RateControlMode RateControl = RateControlMode.Default,
    int QualityLevel = 70,
    int KeyframeIntervalSeconds = 2,
    int SourceWidth = 0,
    int SourceHeight = 0)
{
    /// <summary>Capture-side dimensions; default (0) means same as output.</summary>
    public int EffectiveSourceWidth => SourceWidth > 0 ? SourceWidth : Width;
    public int EffectiveSourceHeight => SourceHeight > 0 ? SourceHeight : Height;

    /// <summary>H.264/HEVC require even dimensions; scaling can produce odd values.</summary>
    public static int AlignEven(int value) => Math.Max(2, value & ~1);

    /// <summary>
    /// Default bitrate heuristic: ~0.09 bits per pixel per frame gives visually clean
    /// H.264 for desktop/game content (≈45 Mbps at 4K60), clamped to sane bounds.
    /// HEVC gets ~35 % less for equivalent quality.
    /// </summary>
    public static int AutoBitrate(int width, int height, int fps, VideoCodec codec = VideoCodec.H264)
    {
        double bitsPerPixel = codec == VideoCodec.Hevc ? 0.06 : 0.09;
        long bits = (long)(width * (long)height * fps * bitsPerPixel);
        return (int)Math.Clamp(bits, 2_000_000, 120_000_000);
    }
}

/// <summary>PCM format delivered by the audio capture side (WASAPI mix format: 32-bit float).</summary>
public sealed record AudioInputFormat(int SampleRate, int Channels)
{
    public int BytesPerSecond => SampleRate * Channels * 4;
    public int BlockAlign => Channels * 4;
}
