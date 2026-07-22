using Recorder.Capture;
using Recorder.Common.Logging;
using Recorder.Graphics;
using Serilog.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Recorder.ScreenshotPoc;

/// <summary>
/// M0 exit-gate tool: captures one frame from every attached monitor through the
/// Windows.Graphics.Capture pipeline and writes each as a PNG. If this produces
/// correct images on multi-monitor / mixed-DPI machines, the capture foundation for
/// the recorder is proven.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Enumerates monitors, captures each one, and reports a per-monitor pass/fail
    /// summary. Failures on one monitor don't abort the others — on multi-GPU systems
    /// a secondary display can fail independently, and we want that diagnosis.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        string outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(outputDirectory);

        using Logger log = RecorderLog.CreateLogger(Path.Combine(outputDirectory, "logs"), verbose: true);

        IReadOnlyList<MonitorInfo> monitors = MonitorEnumeration.GetActiveMonitors();
        log.Information("Found {Count} monitor(s)", monitors.Count);

        using var graphicsDevice = new D3D11GraphicsDevice();
        log.Information("D3D11 device created on adapter: {Adapter}", graphicsDevice.GetAdapterDescription());

        int failures = 0;
        foreach (MonitorInfo monitor in monitors)
        {
            string label = monitor.DeviceName.TrimStart('\\', '.') + (monitor.IsPrimary ? " (primary)" : "");
            try
            {
                CpuBgraBitmap bitmap = await SingleFrameScreenshotCapturer.CaptureAsync(
                    graphicsDevice, monitor, timeout: TimeSpan.FromSeconds(5));

                string fileName = $"screenshot-{SanitizeForFileName(monitor.DeviceName)}.png";
                string filePath = Path.Combine(outputDirectory, fileName);
                await SavePngAsync(bitmap, filePath);

                log.Information("{Monitor}: captured {Width}x{Height} -> {File}",
                    label, bitmap.Width, bitmap.Height, filePath);
            }
            catch (Exception ex)
            {
                failures++;
                log.Error(ex, "{Monitor}: capture FAILED", label);
            }
        }

        log.Information("Done: {Ok}/{Total} monitors captured successfully",
            monitors.Count - failures, monitors.Count);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Encodes tightly packed BGRA pixels as a PNG using the in-box WinRT
    /// BitmapEncoder (no image library dependency), then copies the encoded bytes out
    /// to a regular .NET file stream.
    /// </summary>
    private static async Task SavePngAsync(CpuBgraBitmap bitmap, string filePath)
    {
        using var encodedStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encodedStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)bitmap.Width,
            (uint)bitmap.Height,
            dpiX: 96,
            dpiY: 96,
            bitmap.Pixels);
        await encoder.FlushAsync();

        encodedStream.Seek(0);
        using Stream readStream = encodedStream.AsStreamForRead();
        using FileStream fileStream = File.Create(filePath);
        await readStream.CopyToAsync(fileStream);
    }

    private static string SanitizeForFileName(string deviceName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(deviceName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('_');
    }
}
