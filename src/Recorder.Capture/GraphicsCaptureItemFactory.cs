using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Recorder.Capture;

/// <summary>
/// Creates <see cref="GraphicsCaptureItem"/>s from Win32 handles (HMONITOR/HWND).
/// The public WinRT surface only offers an interactive picker UI; programmatic
/// creation goes through the COM interop interface on the class's activation
/// factory, which is what this type encapsulates.
/// </summary>
public static class GraphicsCaptureItemFactory
{
    private static readonly Guid GraphicsCaptureItemInterfaceId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid interfaceId);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid interfaceId);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid interfaceId, out IntPtr factory);

    public static GraphicsCaptureItem CreateForMonitor(IntPtr monitorHandle)
        => CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForMonitor(monitorHandle, ref iid));

    public static GraphicsCaptureItem CreateForWindow(IntPtr windowHandle)
        => CreateItem((IGraphicsCaptureItemInterop interop, ref Guid iid) => interop.CreateForWindow(windowHandle, ref iid));

    private delegate IntPtr InteropItemCreator(IGraphicsCaptureItemInterop interop, ref Guid interfaceId);

    /// <summary>
    /// Shared plumbing for both handle kinds: fetch the GraphicsCaptureItem activation
    /// factory (via a manually managed HSTRING, which must be deleted afterwards),
    /// query its interop interface, create the item, and wrap the returned ABI pointer
    /// in the projected class — releasing the raw pointer once the wrapper holds its
    /// own reference.
    /// </summary>
    private static GraphicsCaptureItem CreateItem(InteropItemCreator createItem)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out IntPtr classNameHString));
        try
        {
            Guid interopInterfaceId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classNameHString, ref interopInterfaceId, out IntPtr factoryPointer));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
                Guid itemInterfaceId = GraphicsCaptureItemInterfaceId;
                IntPtr itemAbiPointer = createItem(interop, ref itemInterfaceId);
                try
                {
                    return GraphicsCaptureItem.FromAbi(itemAbiPointer);
                }
                finally
                {
                    Marshal.Release(itemAbiPointer);
                }
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }
        }
        finally
        {
            WindowsDeleteString(classNameHString);
        }
    }
}
