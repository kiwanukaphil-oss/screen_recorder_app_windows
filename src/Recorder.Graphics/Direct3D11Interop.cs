using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Recorder.Graphics;

/// <summary>
/// COM bridge between the classic D3D11/DXGI world (Vortice) and the WinRT
/// Direct3D11 wrapper types that Windows.Graphics.Capture consumes/produces.
/// Both directions are pointer casts under the hood — no copies, no conversion cost.
/// </summary>
public static class Direct3D11Interop
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>
    /// COM interface implemented by WinRT Direct3D surface/device wrappers that hands
    /// back the underlying DXGI interface. Declared here because it lives in
    /// windows.graphics.directx.direct3d11.interop.h and has no managed projection.
    /// </summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid interfaceId);
    }

    /// <summary>
    /// Wraps a DXGI device in the WinRT <see cref="IDirect3DDevice"/> that the
    /// Windows.Graphics.Capture frame pool requires. The returned wrapper shares the
    /// same underlying device; the raw ABI pointer is released once the managed
    /// wrapper has taken its own reference.
    /// </summary>
    public static IDirect3DDevice CreateWinRtDevice(IDXGIDevice dxgiDevice)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr abiPointer);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(abiPointer);
        }
        finally
        {
            Marshal.Release(abiPointer);
        }
    }

    /// <summary>
    /// Extracts the D3D11 texture backing a capture frame's WinRT surface.
    /// The returned Vortice wrapper owns one reference and must be disposed.
    /// </summary>
    public static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid textureGuid = typeof(ID3D11Texture2D).GUID;
        IntPtr texturePointer = access.GetInterface(ref textureGuid);
        return new ID3D11Texture2D(texturePointer);
    }
}
