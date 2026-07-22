using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Recorder.Encoding;

/// <summary>
/// Writes an .mp4 file with one H.264 video stream (hardware-encoded when the GPU
/// offers an encoder MFT — NVENC/AMF/Quick Sync — with automatic software fallback)
/// and one AAC audio stream. Video frames enter as GPU textures and reach the encoder
/// through the DXGI device manager without a CPU copy; the sink writer internally
/// inserts the GPU video processor to convert BGRA → NV12.
///
/// Threading contract: all Write*/FinalizeFile calls must come from ONE thread (the
/// session's mux thread) — IMFSinkWriter is not thread-safe.
/// </summary>
public sealed class Mp4SinkWriter : IDisposable
{
    private const uint InterlaceModeProgressive = 2;
    private const uint H264ProfileHigh = 100; // eAVEncH264VProfile_High
    private const int AacBytesPerSecond = 24_000; // 192 kbps stereo, an allowed MF AAC encoder rate

    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IMFSinkWriter _writer;
    private readonly int _videoStreamIndex;
    private readonly int _audioStreamIndex = -1;
    private readonly long _videoFrameDuration100Ns;
    private readonly AudioInputFormat? _audioFormat;
    private bool _finalized;

    public VideoEncodingConfig VideoConfig { get; }

    public Mp4SinkWriter(string outputPath, ID3D11Device device, VideoEncodingConfig video, AudioInputFormat? audio)
    {
        MediaFoundationRuntime.AddRef();
        VideoConfig = video;
        _audioFormat = audio;
        _videoFrameDuration100Ns = 10_000_000L / video.FramesPerSecond;

        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device).CheckError();

        using IMFAttributes writerAttributes = MediaFactory.MFCreateAttributes(4);
        writerAttributes.Set(SinkWriterAttributeKeys.D3DManager, _deviceManager);
        writerAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        // Throttling would block WriteSample to pace input against encoder speed; our
        // bounded queue upstream is the pacing mechanism, so writing must never block.
        writerAttributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);

        _writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, writerAttributes);

        _videoStreamIndex = AddVideoStream(video);
        if (audio is not null)
        {
            _audioStreamIndex = AddAudioStream(audio);
        }

        _writer.BeginWriting();
    }

    /// <summary>
    /// Declares the compressed H.264 output stream and the uncompressed BGRA input the
    /// capture side delivers. FrameSize/FrameRate/PixelAspectRatio are UINT64
    /// attributes packing two 32-bit halves; the positive DefaultStride on the input
    /// type declares top-down row order, matching D3D11 textures (without it MF assumes
    /// bottom-up RGB and the video comes out vertically flipped).
    /// </summary>
    private int AddVideoStream(VideoEncodingConfig video)
    {
        using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)video.BitrateBitsPerSecond);
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceModeProgressive);
        outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(video.Width, video.Height));
        outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(video.FramesPerSecond, 1));
        outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileHigh);
        int streamIndex = _writer.AddStream(outputType);

        using IMFMediaType inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceModeProgressive);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(video.Width, video.Height));
        inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(video.FramesPerSecond, 1));
        inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        inputType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(video.Width * 4));
        _writer.SetInputMediaType(streamIndex, inputType, null);

        return streamIndex;
    }

    /// <summary>
    /// Declares AAC output and the float-PCM input WASAPI delivers. Output is always
    /// stereo/16-bit at the closest supported sample rate; when the input differs
    /// (e.g. 5.1 float), the sink writer inserts the MF resampler to downmix/convert.
    /// </summary>
    private int AddAudioStream(AudioInputFormat audio)
    {
        int outputRate = audio.SampleRate is 44_100 or 48_000 ? audio.SampleRate : 48_000;

        using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        outputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        outputType.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        outputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)outputRate);
        outputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        outputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)AacBytesPerSecond);
        int streamIndex = _writer.AddStream(outputType);

        using IMFMediaType inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
        inputType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Channels);
        inputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.SampleRate);
        inputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 32u);
        inputType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)audio.BlockAlign);
        inputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)audio.BytesPerSecond);
        inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        _writer.SetInputMediaType(streamIndex, inputType, null);

        return streamIndex;
    }

    /// <summary>
    /// Submits one BGRA GPU texture as a video sample. The DXGI surface buffer wraps
    /// the texture by reference (no copy) and Media Foundation keeps the texture alive
    /// via COM ref-counting until the encoder has consumed it, so the caller may
    /// release its own reference immediately after this returns.
    /// </summary>
    public void WriteVideoFrame(ID3D11Texture2D frameTexture, long timestamp100Ns)
    {
        using IMFMediaBuffer buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            typeof(ID3D11Texture2D).GUID, frameTexture, 0, bottomUpWhenLinear: false);

        using (IMF2DBuffer? buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>())
        {
            if (buffer2D is not null)
            {
                buffer.CurrentLength = buffer2D.ContiguousLength;
            }
        }

        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100Ns;
        sample.SampleDuration = _videoFrameDuration100Ns;
        _writer.WriteSample(_videoStreamIndex, sample);
    }

    /// <summary>
    /// Submits a chunk of float-PCM audio. Bytes are copied into an MF memory buffer
    /// (audio volume is tiny next to video, so this copy is irrelevant to performance).
    /// </summary>
    public void WriteAudioChunk(byte[] pcmData, int byteCount, long timestamp100Ns)
    {
        if (_audioStreamIndex < 0 || byteCount == 0)
        {
            return;
        }

        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(byteCount);
        buffer.Lock(out IntPtr bufferPointer, out _, out _);
        try
        {
            Marshal.Copy(pcmData, 0, bufferPointer, byteCount);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = byteCount;

        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100Ns;
        sample.SampleDuration = byteCount * 10_000_000L / _audioFormat!.BytesPerSecond;
        _writer.WriteSample(_audioStreamIndex, sample);
    }

    /// <summary>Flushes pending samples and writes the MP4 moov box. Must be called before Dispose.</summary>
    public void FinalizeFile()
    {
        if (!_finalized)
        {
            _finalized = true;
            _writer.Finalize();
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _deviceManager.Dispose();
        MediaFoundationRuntime.Release();
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
