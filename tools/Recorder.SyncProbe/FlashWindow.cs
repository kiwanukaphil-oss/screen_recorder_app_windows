using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Recorder.SyncProbe;

/// <summary>
/// Borderless topmost window covering a monitor, driven by a DXGI flip-model
/// swapchain so the probe controls exactly which vblank shows white vs black.
/// Present(1) blocks until the frame is queued at vsync, so a QPC reading taken
/// right after Present is within one refresh of actual scanout — that reading is
/// the flash's ground-truth event time.
/// </summary>
public sealed class FlashWindow : IDisposable
{
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    // Kept in a static field so the GC never collects the delegate the window class
    // points at for the lifetime of the process.
    private static readonly WndProcDelegate StaticWndProc = DefWindowProcW;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref NativeMessage message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint PmRemove = 1;

    private readonly IntPtr _hwnd;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11RenderTargetView _renderTarget;

    /// <summary>
    /// Creates the window at the monitor's bounds and a flip-model swapchain on it.
    /// The window class is registered once per process; the swapchain uses two
    /// buffers because the probe only ever shows solid colors.
    /// </summary>
    public FlashWindow(ID3D11Device device, int left, int top, int width, int height)
    {
        var windowClass = new WNDCLASSEXW
        {
            Size = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(StaticWndProc),
            Instance = Marshal.GetHINSTANCE(typeof(FlashWindow).Module),
            ClassName = "RecorderSyncProbeWindow",
        };
        RegisterClassExW(ref windowClass);

        _hwnd = CreateWindowExW(
            WsExTopmost, windowClass.ClassName, "Sync Probe", WsPopup | WsVisible,
            left, top, width, height, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create the probe window.");
        }
        ShowWindow(_hwnd, 5);

        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var swapChainDescription = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        };
        _swapChain = factory.CreateSwapChainForHwnd(device, _hwnd, swapChainDescription);

        _context = device.ImmediateContext;
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>Presents a solid white or black frame at the next vsync (blocking until queued).</summary>
    public void Present(bool white)
    {
        _context.OMSetRenderTargets(_renderTarget);
        _context.ClearRenderTargetView(_renderTarget, white ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 1f));
        _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>Drains pending window messages so the window never registers as unresponsive.</summary>
    public void PumpMessages()
    {
        while (PeekMessageW(out NativeMessage message, _hwnd, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    public void Dispose()
    {
        _renderTarget.Dispose();
        _swapChain.Dispose();
        DestroyWindow(_hwnd);
    }
}
