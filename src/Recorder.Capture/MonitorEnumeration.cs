using System.Runtime.InteropServices;

namespace Recorder.Capture;

/// <summary>One attached display, as reported by the Win32 monitor APIs.</summary>
public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

/// <summary>
/// Enumerates physical displays via EnumDisplayMonitors. Win32 rather than WinRT
/// because the HMONITOR handle is exactly what the Windows.Graphics.Capture interop
/// factory needs to create a capture item for a monitor.
/// </summary>
public static class MonitorEnumeration
{
    private const int MonitorInfoFlagPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfoEx
    {
        public int Size;
        public NativeRect MonitorRect;
        public NativeRect WorkRect;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumCallback(IntPtr monitorHandle, IntPtr hdc, ref NativeRect rect, IntPtr callbackData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumCallback callback, IntPtr callbackData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitorHandle, ref NativeMonitorInfoEx info);

    /// <summary>
    /// Returns all active monitors with their desktop-space bounds. The callback style
    /// of EnumDisplayMonitors is flattened into a plain list here so callers never
    /// touch Win32 details; the delegate is kept in a local to prevent the GC from
    /// collecting it while the native enumeration is still running.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> GetActiveMonitors()
    {
        var monitors = new List<MonitorInfo>();

        MonitorEnumCallback collectMonitor = (IntPtr handle, IntPtr _, ref NativeRect _, IntPtr _) =>
        {
            var info = new NativeMonitorInfoEx { Size = Marshal.SizeOf<NativeMonitorInfoEx>() };
            if (GetMonitorInfoW(handle, ref info))
            {
                monitors.Add(new MonitorInfo(
                    handle,
                    info.DeviceName,
                    info.MonitorRect.Left,
                    info.MonitorRect.Top,
                    info.MonitorRect.Right - info.MonitorRect.Left,
                    info.MonitorRect.Bottom - info.MonitorRect.Top,
                    (info.Flags & MonitorInfoFlagPrimary) != 0));
            }
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collectMonitor, IntPtr.Zero);
        GC.KeepAlive(collectMonitor);
        return monitors;
    }
}
