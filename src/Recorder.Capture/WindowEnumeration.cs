using System.Runtime.InteropServices;
using System.Text;

namespace Recorder.Capture;

/// <summary>A top-level window that can be offered as a capture source.</summary>
public sealed record CapturableWindow(IntPtr Handle, string Title);

/// <summary>
/// Lists windows worth showing in a capture picker: visible, titled, top-level,
/// not tool windows, and not cloaked (UWP/system windows that exist but are not
/// actually on screen report themselves cloaked via DWM).
/// </summary>
public static class WindowEnumeration
{
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int DwmwaCloaked = 14;

    /// <summary>
    /// Snapshot of currently capturable windows. The callback filters as it walks:
    /// invisible, untitled, tool and cloaked windows are skipped so the picker only
    /// shows things a user recognizes as "apps".
    /// </summary>
    public static IReadOnlyList<CapturableWindow> GetCapturableWindows()
    {
        var windows = new List<CapturableWindow>();
        var titleBuffer = new StringBuilder(512);

        EnumWindowsCallback collectWindow = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }
            if ((GetWindowLongW(hwnd, GwlExstyle) & WsExToolwindow) != 0)
            {
                return true;
            }
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            titleBuffer.Clear();
            if (GetWindowTextW(hwnd, titleBuffer, titleBuffer.Capacity) == 0)
            {
                return true;
            }

            windows.Add(new CapturableWindow(hwnd, titleBuffer.ToString()));
            return true;
        };

        EnumWindows(collectWindow, IntPtr.Zero);
        GC.KeepAlive(collectWindow);
        return windows;
    }
}
