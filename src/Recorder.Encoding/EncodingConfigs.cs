namespace Recorder.Encoding;

/// <summary>Resolved video encoding parameters for one recording (no "auto" values left).</summary>
public sealed record VideoEncodingConfig(int Width, int Height, int FramesPerSecond, int BitrateBitsPerSecond)
{
    /// <summary>
    /// Default bitrate heuristic: ~0.09 bits per pixel per frame gives visually clean
    /// H.264 for desktop/game content (≈45 Mbps at 4K60), clamped to sane bounds.
    /// </summary>
    public static int AutoBitrate(int width, int height, int fps)
    {
        long bits = (long)(width * (long)height * fps * 0.09);
        return (int)Math.Clamp(bits, 4_000_000, 120_000_000);
    }
}

/// <summary>PCM format delivered by the audio capture side (WASAPI mix format: 32-bit float).</summary>
public sealed record AudioInputFormat(int SampleRate, int Channels)
{
    public int BytesPerSecond => SampleRate * Channels * 4;
    public int BlockAlign => Channels * 4;
}
