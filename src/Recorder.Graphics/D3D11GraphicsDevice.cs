using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace Recorder.Graphics;

/// <summary>A CPU-side copy of a BGRA frame, tightly packed (stride == width * 4).</summary>
public sealed record CpuBgraBitmap(byte[] Pixels, int Width, int Height);

/// <summary>
/// Owns the single D3D11 device shared by capture, GPU color conversion and (later)
/// the hardware encoder. One device for the whole pipeline is what makes the
/// zero-copy hand-off between those stages possible.
/// </summary>
public sealed class D3D11GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>WinRT-facing wrapper of the same device, required by Windows.Graphics.Capture.</summary>
    public IDirect3DDevice WinRtDevice { get; }

    /// <summary>
    /// Creates the hardware D3D11 device. BgraSupport is mandatory for
    /// Windows.Graphics.Capture surfaces; VideoSupport prepares the device for the
    /// Media Foundation encoder sharing that arrives in M1.
    /// </summary>
    public D3D11GraphicsDevice()
    {
        FeatureLevel[] featureLevels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        };

        D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            featureLevels,
            out ID3D11Device? device).CheckError();

        Device = device!;
        Context = Device.ImmediateContext;

        using IDXGIDevice dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        WinRtDevice = Direct3D11Interop.CreateWinRtDevice(dxgiDevice);
    }

    /// <summary>Name of the GPU this device runs on, for logs and diagnostics.</summary>
    public string GetAdapterDescription()
    {
        using IDXGIDevice dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        return adapter.Description.Description;
    }

    /// <summary>
    /// Copies a GPU texture to CPU memory as tightly packed BGRA bytes.
    /// This readback is for screenshots and tests ONLY — the recording hot path never
    /// calls it (frames go GPU → encoder, see PLAN.md principle 1). The copy goes
    /// through a staging texture because only staging resources are CPU-mappable, and
    /// rows are compacted because mapped textures pad each row to RowPitch.
    /// </summary>
    public CpuBgraBitmap CopyTextureToCpu(ID3D11Texture2D gpuTexture)
    {
        Texture2DDescription sourceDesc = gpuTexture.Description;
        var stagingDesc = new Texture2DDescription
        {
            Width = sourceDesc.Width,
            Height = sourceDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = sourceDesc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using ID3D11Texture2D staging = Device.CreateTexture2D(stagingDesc);
        Context.CopyResource(staging, gpuTexture);

        MappedSubresource mapped = Context.Map(staging, 0, MapMode.Read);
        try
        {
            int width = (int)sourceDesc.Width;
            int height = (int)sourceDesc.Height;
            int packedRowBytes = width * 4;
            byte[] pixels = new byte[packedRowBytes * height];

            for (int row = 0; row < height; row++)
            {
                IntPtr sourceRow = mapped.DataPointer + row * (nint)mapped.RowPitch;
                Marshal.Copy(sourceRow, pixels, row * packedRowBytes, packedRowBytes);
            }

            return new CpuBgraBitmap(pixels, width, height);
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        WinRtDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
