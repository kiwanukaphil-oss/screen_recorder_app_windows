using Recorder.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Recorder.Capture;

/// <summary>
/// Streams every frame of a monitor via Windows.Graphics.Capture. Event-driven: WGC
/// only raises FrameArrived when screen content changed, so a static desktop costs
/// almost nothing (see PLAN.md — idle-screen efficiency). Each frame is copied on the
/// GPU into a texture the receiver owns, because WGC recycles its own pool textures
/// as soon as the frame object is disposed.
/// </summary>
public sealed class ContinuousMonitorCaptureSource : IDisposable
{
    /// <summary>Receives an owned GPU texture + the frame's QPC timestamp (100 ns). Runs on the WGC thread.</summary>
    public delegate void FrameReadyCallback(ID3D11Texture2D ownedFrameTexture, long timestamp100Ns);

    private readonly D3D11GraphicsDevice _graphicsDevice;
    private readonly GraphicsCaptureItem _captureItem;
    private readonly bool _captureCursor;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private FrameReadyCallback? _onFrameReady;
    private long _framesDelivered;

    public int Width { get; }
    public int Height { get; }
    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);

    public ContinuousMonitorCaptureSource(D3D11GraphicsDevice graphicsDevice, MonitorInfo monitor, bool captureCursor)
    {
        _graphicsDevice = graphicsDevice;
        _captureCursor = captureCursor;
        _captureItem = GraphicsCaptureItemFactory.CreateForMonitor(monitor.Handle);
        Width = _captureItem.Size.Width;
        Height = _captureItem.Size.Height;
    }

    /// <summary>
    /// Starts the capture session with a free-threaded frame pool (no UI dispatcher
    /// required; callbacks arrive on a WGC worker thread). Two pool buffers are enough
    /// because every frame is copied out immediately in the callback.
    /// </summary>
    public void Start(FrameReadyCallback onFrameReady)
    {
        if (_session is not null)
        {
            throw new InvalidOperationException("Capture source is already started.");
        }

        _onFrameReady = onFrameReady;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _graphicsDevice.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            _captureItem.Size);
        _framePool.FrameArrived += HandleFrameArrived;

        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.IsCursorCaptureEnabled = _captureCursor;
        _session.StartCapture();
    }

    /// <summary>
    /// Copies the arrived frame into a receiver-owned texture and forwards it with the
    /// frame's own QPC timestamp (SystemRelativeTime — stamped by the compositor at
    /// present time, which is more accurate than reading a clock here). Frames whose
    /// size no longer matches the pool (resolution change mid-recording) are skipped;
    /// M3 adds pool recreation for that case.
    /// </summary>
    private void HandleFrameArrived(Direct3D11CaptureFramePool pool, object? args)
    {
        using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
        if (frame is null || _onFrameReady is null)
        {
            return;
        }

        if (frame.ContentSize.Width != Width || frame.ContentSize.Height != Height)
        {
            return;
        }

        using ID3D11Texture2D sourceTexture = Direct3D11Interop.GetTextureFromSurface(frame.Surface);

        var copyDescription = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // ShaderResource lets the encoder's GPU color converter read the texture directly.
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        ID3D11Texture2D ownedCopy = _graphicsDevice.Device.CreateTexture2D(copyDescription);
        _graphicsDevice.Context.CopyResource(ownedCopy, sourceTexture);

        Interlocked.Increment(ref _framesDelivered);
        _onFrameReady(ownedCopy, frame.SystemRelativeTime.Ticks);
    }

    /// <summary>Stops delivering frames. Safe to call more than once.</summary>
    public void Stop()
    {
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= HandleFrameArrived;
        }
        _onFrameReady = null;
        _session?.Dispose();
        _framePool?.Dispose();
        _session = null;
        _framePool = null;
    }

    public void Dispose()
    {
        // GraphicsCaptureItem has no Close/Dispose; its native resources are released
        // by the garbage collector once the session and frame pool are gone.
        Stop();
    }
}
