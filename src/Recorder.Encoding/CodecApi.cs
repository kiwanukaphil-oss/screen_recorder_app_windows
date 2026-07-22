using System.Runtime.InteropServices;

namespace Recorder.Encoding;

/// <summary>
/// Minimal ICodecAPI interop for configuring the encoder MFT behind the sink writer.
/// Rate-control mode, quality level and GOP size are not settable through media-type
/// attributes — ICodecAPI (obtained via IMFSinkWriter.GetServiceForStream) is the
/// documented path. The vtable order below must match codecapi.h exactly.
/// </summary>
public static class CodecApi
{
    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api, out object valueMin, out object valueMax, out object steppingDelta);
        [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint valuesCount);
        [PreserveSig] int GetDefaultValue(ref Guid api, out object value);
        [PreserveSig] int GetValue(ref Guid api, out object value);
        [PreserveSig] int SetValue(ref Guid api, [In] ref object value);
        [PreserveSig] int RegisterForEvent(ref Guid api, IntPtr userData);
        [PreserveSig] int UnregisterForEvent(ref Guid api);
        [PreserveSig] int SetAllDefaults();
        [PreserveSig] int SetValueWithNotify(ref Guid api, [In] ref object value, out IntPtr changedParams, out uint changedParamCount);
        [PreserveSig] int SetAllDefaultsWithNotify(out IntPtr changedParams, out uint changedParamCount);
        [PreserveSig] int GetAllSettings(IntPtr stream);
        [PreserveSig] int SetAllSettings(IntPtr stream);
        [PreserveSig] int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changedParams, out uint changedParamCount);
    }

    // CODECAPI_* property GUIDs from codecapi.h.
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423"); // AVEncCommonRateControlMode
    public static readonly Guid Quality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");         // AVEncCommonQuality (1-100)
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");     // AVEncCommonMeanBitRate
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");         // AVEncMPVGOPSize (frames)

    // eAVEncCommonRateControlMode values.
    public const uint ModeCbr = 0;
    public const uint ModePeakConstrainedVbr = 1;
    public const uint ModeUnconstrainedVbr = 2;
    public const uint ModeQuality = 3;

    /// <summary>
    /// Sets one UINT32 codec property, returning false when the encoder does not
    /// support or accept it. Failures are expected and non-fatal: encoder MFTs from
    /// different vendors (and the software fallback) support different subsets, and
    /// recording must proceed with encoder defaults rather than fail.
    /// </summary>
    internal static bool TrySetUInt32(ICodecAPI api, Guid property, uint value)
    {
        Guid propertyCopy = property;
        if (api.IsSupported(ref propertyCopy) != 0)
        {
            return false;
        }
        object boxed = value;
        return api.SetValue(ref propertyCopy, ref boxed) == 0;
    }
}
