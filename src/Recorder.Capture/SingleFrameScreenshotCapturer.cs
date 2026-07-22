using Recorder.Graphics;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Recorder.Capture;

/// <summary>
/// M0 proof-of-concept: captures exactly one frame of a monitor through the same
/// Windows.Graphics.Capture path the real recorder will use, and returns it as CPU
/// pixels. Proves the full interop chain (HMONITOR → capture item → D3D11 texture →
/// readback) before any encoder work begins.
/// </summary>
public static class SingleFrameScreenshotCapturer
{
    /// <summary>
    /// Starts a capture session on the monitor, waits for the first frame, copies it
    /// to CPU memory and tears the session down. Uses the free-threaded frame pool so
    /// no UI dispatcher is needed (this must work from console tools and services).
    /// A TaskCompletionSource bridges the FrameArrived callback to async/await; the
    /// timeout guards against configurations where frames never arrive (e.g. capture
    /// blocked by policy) so callers get a clear error instead of a hang.
    /// </summary>
    public static async Task<CpuBgraBitmap> CaptureAsync(
        D3D11GraphicsDevice graphicsDevice,
        MonitorInfo monitor,
        TimeSpan timeout)
    {
        GraphicsCaptureItem captureItem = GraphicsCaptureItemFactory.CreateForMonitor(monitor.Handle);

        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            graphicsDevice.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            captureItem.Size);

        var firstFrame = new TaskCompletionSource<CpuBgraBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);

        framePool.FrameArrived += (pool, _) =>
        {
            // Only the first frame matters here; later frames are drained and ignored.
            using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame is null || firstFrame.Task.IsCompleted)
            {
                return;
            }

            try
            {
                using ID3D11Texture2D texture = Direct3D11Interop.GetTextureFromSurface(frame.Surface);
                firstFrame.TrySetResult(graphicsDevice.CopyTextureToCpu(texture));
            }
            catch (Exception ex)
            {
                firstFrame.TrySetException(ex);
            }
        };

        using GraphicsCaptureSession session = framePool.CreateCaptureSession(captureItem);
        session.IsCursorCaptureEnabled = true;
        session.StartCapture();

        Task completed = await Task.WhenAny(firstFrame.Task, Task.Delay(timeout));
        if (completed != firstFrame.Task)
        {
            throw new TimeoutException(
                $"No capture frame arrived from monitor '{monitor.DeviceName}' within {timeout.TotalSeconds:0.#}s.");
        }

        return await firstFrame.Task;
    }
}
