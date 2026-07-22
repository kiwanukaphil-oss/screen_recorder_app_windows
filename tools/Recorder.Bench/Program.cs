using System.Diagnostics;
using Recorder.Encoding;
using Recorder.Graphics;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Recorder.Bench;

/// <summary>
/// Encoder-path benchmark: pushes synthetic GPU frames straight into the
/// Mp4SinkWriter as fast as the encoder accepts them (writer throttling ON, so
/// submission rate == real encoder throughput). This isolates the encode side of the
/// M2 4K120 gate from the capture side — WGC can never deliver more frames than the
/// display refreshes, so capture at 120 Hz additionally requires a 120 Hz monitor.
///
/// Usage: Recorder.Bench [--width N] [--height N] [--fps N] [--seconds N] [--codec h264|hevc] [--bitrate KBPS]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int width = ReadInt(args, "--width") ?? 3840;
        int height = ReadInt(args, "--height") ?? 2160;
        int fps = ReadInt(args, "--fps") ?? 120;
        int seconds = ReadInt(args, "--seconds") ?? 15;
        VideoCodec codec = (ReadString(args, "--codec") ?? "h264").Equals("hevc", StringComparison.OrdinalIgnoreCase)
            ? VideoCodec.Hevc
            : VideoCodec.H264;
        int bitrate = (ReadInt(args, "--bitrate") ?? 0) is int kbps and > 0
            ? kbps * 1000
            : VideoEncodingConfig.AutoBitrate(width, height, fps, codec);

        string outputPath = Path.Combine(Path.GetTempPath(), $"recorder-bench-{Guid.NewGuid():N}.mp4");
        try
        {
            RunEncoderThroughputBench(width, height, fps, seconds, codec, bitrate, outputPath);
            return 0;
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>
    /// Pre-renders a ring of distinct frames (animated color gradient — cheap to make,
    /// non-trivial to encode), then measures how fast the sink writer + encoder chain
    /// accepts them. Timestamps advance at the nominal frame interval regardless of
    /// wall clock, mirroring what a 120 Hz capture would submit. Reports sustained fps
    /// versus the target and the realtime headroom factor.
    /// </summary>
    private static void RunEncoderThroughputBench(
        int width, int height, int fps, int seconds, VideoCodec codec, int bitrate, string outputPath)
    {
        using var graphicsDevice = new D3D11GraphicsDevice();
        Console.WriteLine($"GPU: {graphicsDevice.GetAdapterDescription()}");
        Console.WriteLine($"Bench: {width}x{height}@{fps} {codec} {bitrate / 1_000_000.0:0.#} Mbps, {seconds} s of frames");

        const int RingSize = 16;
        var textures = new ID3D11Texture2D[RingSize];
        var renderTargets = new ID3D11RenderTargetView[RingSize];
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        for (int i = 0; i < RingSize; i++)
        {
            textures[i] = graphicsDevice.Device.CreateTexture2D(description);
            renderTargets[i] = graphicsDevice.Device.CreateRenderTargetView(textures[i]);
            graphicsDevice.Context.ClearRenderTargetView(
                renderTargets[i],
                new Color4(i / (float)RingSize, 1f - i / (float)RingSize, (i * 37 % RingSize) / (float)RingSize, 1f));
        }
        graphicsDevice.Context.Flush();

        var config = new VideoEncodingConfig(width, height, fps, bitrate, codec);
        using var sink = new Mp4SinkWriter(outputPath, graphicsDevice.Device, config, blockOnEncoderBackpressure: true);
        Console.WriteLine($"Encoder properties: {sink.AppliedEncoderProperties}");

        int totalFrames = fps * seconds;
        long frameInterval100Ns = 10_000_000L / fps;
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < totalFrames; i++)
        {
            sink.WriteVideoFrame(textures[i % RingSize], i * frameInterval100Ns);
        }
        sink.FinalizeFile();
        stopwatch.Stop();

        foreach (ID3D11RenderTargetView rtv in renderTargets) rtv.Dispose();
        foreach (ID3D11Texture2D texture in textures) texture.Dispose();

        double achievedFps = totalFrames / stopwatch.Elapsed.TotalSeconds;
        double headroom = achievedFps / fps;
        long outputBytes = new FileInfo(outputPath).Length;
        Console.WriteLine($"Submitted {totalFrames} frames in {stopwatch.Elapsed.TotalSeconds:0.00} s");
        Console.WriteLine($"Encoder throughput: {achievedFps:0.0} fps = {headroom:0.00}x realtime at {fps} fps target");
        Console.WriteLine($"Output: {outputBytes / 1_048_576.0:0.0} MB");
        Console.WriteLine(headroom >= 1.0
            ? $"GATE: PASS (encode path sustains {fps} fps)"
            : $"GATE: FAIL (encode path max {achievedFps:0.0} fps < {fps} fps target)");
    }

    private static int? ReadInt(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value) ? value : null;
    }

    private static string? ReadString(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
