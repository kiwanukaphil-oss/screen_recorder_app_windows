using Recorder.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Recorder.Capture;

/// <summary>
/// Streams every frame of a capture item (a monitor or a single window) via
/// Windows.Graphics.Capture. Event-driven: WGC only raises FrameArrived when content
/// changed, so a static source costs almost nothing. Each frame is copied on the GPU
/// into a texture the receiver owns, because WGC recycles its pool textures as soon
/// as the frame object is disposed.
///
/// The output size is FIXED at the item's size at construction — the encoder cannot
/// change resolution mid-stream. When the source is resized (window resize, display
/// mode change), the frame pool is recreated at the new size and each frame's
/// overlapping region is copied into the fixed-size output (short edges letterboxed
/// with black), so the recording survives instead of stopping.
/// </summary>
public sealed class ContinuousCaptureSource : IDisposable
{
    /// <summary>Receives an owned GPU texture + the frame's QPC timestamp (100 ns). Runs on the WGC thread.</summary>
    public delegate void FrameReadyCallback(ID3D11Texture2D ownedFrameTexture, long timestamp100Ns);

    private readonly D3D11GraphicsDevice _graphicsDevice;
    private readonly GraphicsCaptureItem _captureItem;
    private readonly bool _captureCursor;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private FrameReadyCallback? _onFrameReady;
    private Windows.Graphics.SizeInt32 _poolSize;
    private long _framesDelivered;
    private long _sourceResizes;

    public int Width { get; }
    public int Height { get; }
    public string SourceLabel { get; }
    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);

    /// <summary>Times the source changed size mid-capture (diagnostics).</summary>
    public long SourceResizes => Interlocked.Read(ref _sourceResizes);

    public static ContinuousCaptureSource ForMonitor(D3D11GraphicsDevice device, MonitorInfo monitor, bool captureCursor)
        => new(device, GraphicsCaptureItemFactory.CreateForMonitor(monitor.Handle),
               $"monitor {monitor.DeviceName.TrimStart('\\', '.')}", captureCursor);

    public static ContinuousCaptureSource ForWindow(D3D11GraphicsDevice device, CapturableWindow window, bool captureCursor)
        => new(device, GraphicsCaptureItemFactory.CreateForWindow(window.Handle),
               $"window \"{window.Title}\"", captureCursor);

    private ContinuousCaptureSource(D3D11GraphicsDevice graphicsDevice, GraphicsCaptureItem captureItem, string sourceLabel, bool captureCursor)
    {
        _graphicsDevice = graphicsDevice;
        _captureItem = captureItem;
        _captureCursor = captureCursor;
        SourceLabel = sourceLabel;
        // H.264/HEVC need even dimensions; window sizes are arbitrary.
        Width = Math.Max(2, captureItem.Size.Width & ~1);
        Height = Math.Max(2, captureItem.Size.Height & ~1);
    }

    /// <summary>
    /// Starts the capture session with a free-threaded frame pool (no UI dispatcher
    /// required; callbacks arrive on a WGC worker thread).
    /// </summary>
    public void Start(FrameReadyCallback onFrameReady)
    {
        if (_session is not null)
        {
            throw new InvalidOperationException("Capture source is already started.");
        }

        _onFrameReady = onFrameReady;
        _poolSize = _captureItem.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _graphicsDevice.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            _poolSize);
        _framePool.FrameArrived += HandleFrameArrived;

        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.IsCursorCaptureEnabled = _captureCursor;
        _session.StartCapture();
    }

    /// <summary>
    /// Copies the arrived frame into a receiver-owned texture at the FIXED output
    /// size. Same-size frames take the plain CopyResource path; after a resize the
    /// pool is recreated at the source's new size and the overlapping region is
    /// copied (destination pre-cleared to black), which keeps the encoder fed with
    /// constant-size frames. Timestamps come from the frame itself
    /// (SystemRelativeTime — stamped by the compositor at present time).
    /// </summary>
    private void HandleFrameArrived(Direct3D11CaptureFramePool pool, object? args)
    {
        using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
        if (frame is null || _onFrameReady is null)
        {
            return;
        }

        if (frame.ContentSize != _poolSize)
        {
            _poolSize = frame.ContentSize;
            Interlocked.Increment(ref _sourceResizes);
            pool.Recreate(
                _graphicsDevice.WinRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _poolSize);
            // This frame's surface still has the OLD pool size; content beyond the
            // new ContentSize is stale. The min-region copy below handles it either way.
        }

        using ID3D11Texture2D sourceTexture = Direct3D11Interop.GetTextureFromSurface(frame.Surface);
        Texture2DDescription sourceDescription = sourceTexture.Description;

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

        if (sourceDescription.Width == Width && sourceDescription.Height == Height)
        {
            _graphicsDevice.Context.CopyResource(ownedCopy, sourceTexture);
        }
        else
        {
            using ID3D11RenderTargetView clearTarget = _graphicsDevice.Device.CreateRenderTargetView(ownedCopy);
            _graphicsDevice.Context.ClearRenderTargetView(clearTarget, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));

            uint copyWidth = Math.Min((uint)Width, Math.Min(sourceDescription.Width, (uint)Math.Max(1, frame.ContentSize.Width)));
            uint copyHeight = Math.Min((uint)Height, Math.Min(sourceDescription.Height, (uint)Math.Max(1, frame.ContentSize.Height)));
            _graphicsDevice.Context.CopySubresourceRegion(
                ownedCopy, 0, 0, 0, 0,
                sourceTexture, 0,
                new Vortice.Mathematics.Box(0, 0, 0, (int)copyWidth, (int)copyHeight, 1));
        }

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
