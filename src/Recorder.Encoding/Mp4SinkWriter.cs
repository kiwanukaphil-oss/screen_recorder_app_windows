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

    /// <summary>Codec properties actually accepted by the encoder, for logs/diagnostics.</summary>
    public string AppliedEncoderProperties { get; private set; } = "encoder defaults";

    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IMFSinkWriter _writer;
    private readonly IMFMediaSink? _fragmentedSink;
    private readonly IMFByteStream? _fragmentedByteStream;
    private readonly int _videoStreamIndex;
    private readonly int[] _audioStreamIndices;
    private readonly long _videoFrameDuration100Ns;
    private readonly AudioInputFormat[] _audioFormats;
    private bool _finalized;

    public VideoEncodingConfig VideoConfig { get; }

    /// <summary>True when writing crash-safe fragmented MP4 (§: playable up to the last fragment).</summary>
    public bool IsFragmented => _fragmentedSink is not null;

    /// <summary>
    /// Each entry in <paramref name="audioTracks"/> becomes its own AAC track in the
    /// MP4 (track 0 first). Editors can then mix/mute system audio and microphone
    /// independently — the roadmap's "separate audio tracks" requirement.
    /// </summary>
    public Mp4SinkWriter(string outputPath, ID3D11Device device, VideoEncodingConfig video, params AudioInputFormat[] audioTracks)
        : this(outputPath, device, video, blockOnEncoderBackpressure: false, fragmentedContainer: false, audioTracks)
    {
    }

    public Mp4SinkWriter(
        string outputPath,
        ID3D11Device device,
        VideoEncodingConfig video,
        bool blockOnEncoderBackpressure,
        params AudioInputFormat[] audioTracks)
        : this(outputPath, device, video, blockOnEncoderBackpressure, fragmentedContainer: false, audioTracks)
    {
    }

    /// <summary>
    /// <paramref name="blockOnEncoderBackpressure"/>: live recording passes false —
    /// the session's bounded queue is the pacing mechanism and WriteSample must never
    /// block the mux thread. The benchmark harness passes true so that submission
    /// rate equals real encoder throughput.
    /// <paramref name="fragmentedContainer"/>: writes fragmented MP4 — the file is
    /// playable up to the last flushed fragment even after a crash or power loss,
    /// at the cost of supporting at most ONE audio track (a limitation of
    /// MFCreateFMPEG4MediaSink; callers must downgrade or drop tracks first).
    /// </summary>
    public Mp4SinkWriter(
        string outputPath,
        ID3D11Device device,
        VideoEncodingConfig video,
        bool blockOnEncoderBackpressure,
        bool fragmentedContainer,
        params AudioInputFormat[] audioTracks)
    {
        if (fragmentedContainer && audioTracks.Length > 1)
        {
            throw new NotSupportedException(
                "The fragmented-MP4 sink supports at most one audio track; mix or drop tracks first.");
        }

        MediaFoundationRuntime.AddRef();
        VideoConfig = video;
        _audioFormats = audioTracks;
        _videoFrameDuration100Ns = 10_000_000L / video.FramesPerSecond;

        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device).CheckError();

        using IMFAttributes writerAttributes = MediaFactory.MFCreateAttributes(4);
        writerAttributes.Set(SinkWriterAttributeKeys.D3DManager, _deviceManager);
        writerAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        // Throttling would block WriteSample to pace input against encoder speed; our
        // bounded queue upstream is the pacing mechanism, so writing must never block.
        if (!blockOnEncoderBackpressure)
        {
            writerAttributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
        }

        // Encoder properties passed at creation time. Note: the NVENC MFT (driver
        // 576.x, RTX 3090) enforces a 1-second GOP regardless of this store, the
        // media-type MaxKeyframeSpacing, and ICodecAPI — all three are still set
        // because AMF/QSV/software encoders are expected to honor at least one.
        using IMFAttributes encoderConfig = MediaFactory.MFCreateAttributes(1);
        encoderConfig.Set(CodecApi.GopSize, (uint)(video.KeyframeIntervalSeconds * video.FramesPerSecond));
        writerAttributes.Set(SinkWriterAttributeKeys.EncoderConfig, encoderConfig);

        if (!fragmentedContainer)
        {
            _writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, writerAttributes);
            _videoStreamIndex = AddVideoStream(video);
            _audioStreamIndices = _audioFormats.Select(AddAudioStream).ToArray();
        }
        else
        {
            // The fMP4 media sink is created with its final output types up front; the
            // sink writer wraps it and its streams keep the sink's fixed indices
            // (0 = video, 1 = audio).
            using IMFMediaType videoOutputType = CreateVideoOutputType(video);
            using IMFMediaType? audioOutputType = _audioFormats.Length == 1 ? CreateAudioOutputType(_audioFormats[0]) : null;

            _fragmentedByteStream = MediaFactory.MFCreateFile(
                FileAccessMode.MfAccessModeReadwrite, FileOpenMode.MfOpenModeDeleteIfExist, FileFlags.None, outputPath);
            MediaFactory.MFCreateFMPEG4MediaSink(
                _fragmentedByteStream, videoOutputType, audioOutputType, out _fragmentedSink).CheckError();
            _writer = MediaFactory.MFCreateSinkWriterFromMediaSink(_fragmentedSink, writerAttributes);

            _videoStreamIndex = 0;
            using (IMFMediaType videoInputType = CreateVideoInputType(video))
            {
                _writer.SetInputMediaType(_videoStreamIndex, videoInputType, null);
            }
            if (_audioFormats.Length == 1)
            {
                using IMFMediaType audioInputType = CreateAudioInputType(_audioFormats[0]);
                _writer.SetInputMediaType(1, audioInputType, null);
                _audioStreamIndices = new[] { 1 };
            }
            else
            {
                _audioStreamIndices = Array.Empty<int>();
            }
        }

        // The encoder MFT exists once the input type is set; codec properties must be
        // applied before BeginWriting for most encoders to honor them.
        ApplyEncoderProperties(video);

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
        using IMFMediaType outputType = CreateVideoOutputType(video);
        int streamIndex = _writer.AddStream(outputType);
        using IMFMediaType inputType = CreateVideoInputType(video);
        _writer.SetInputMediaType(streamIndex, inputType, null);
        return streamIndex;
    }

    private int AddAudioStream(AudioInputFormat audio)
    {
        using IMFMediaType outputType = CreateAudioOutputType(audio);
        int streamIndex = _writer.AddStream(outputType);
        using IMFMediaType inputType = CreateAudioInputType(audio);
        _writer.SetInputMediaType(streamIndex, inputType, null);
        return streamIndex;
    }

    private static IMFMediaType CreateVideoOutputType(VideoEncodingConfig video)
    {
        IMFMediaType outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype,
            video.Codec == VideoCodec.Hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)video.BitrateBitsPerSecond);
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceModeProgressive);
        outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(video.Width, video.Height));
        outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(video.FramesPerSecond, 1));
        outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        // Declared on the media type as well as via ICodecAPI: some encoder MFTs
        // (NVENC among them) reset ICodecAPI GOP during BeginWriting negotiation,
        // but honor the media-type attribute.
        outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing,
            (uint)(video.KeyframeIntervalSeconds * video.FramesPerSecond));
        if (video.Codec == VideoCodec.H264)
        {
            // HEVC MFTs choose their own profile; forcing one is rejected by some vendors.
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileHigh);
        }
        return outputType;
    }

    /// <summary>
    /// Input type describes what capture delivers; when its size differs from the
    /// output type's, the GPU video processor scales during the same pass it uses for
    /// BGRA → NV12 conversion. The positive DefaultStride declares top-down row order,
    /// matching D3D11 textures (without it MF assumes bottom-up RGB and the video
    /// comes out vertically flipped).
    /// </summary>
    private static IMFMediaType CreateVideoInputType(VideoEncodingConfig video)
    {
        IMFMediaType inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceModeProgressive);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(video.EffectiveSourceWidth, video.EffectiveSourceHeight));
        inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(video.FramesPerSecond, 1));
        inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        inputType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(video.EffectiveSourceWidth * 4));
        return inputType;
    }

    /// <summary>
    /// AAC output: always stereo/16-bit at the closest supported sample rate; when the
    /// input differs (e.g. 5.1 float), the sink writer inserts the MF resampler.
    /// </summary>
    private static IMFMediaType CreateAudioOutputType(AudioInputFormat audio)
    {
        int outputRate = audio.SampleRate is 44_100 or 48_000 ? audio.SampleRate : 48_000;
        IMFMediaType outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        outputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        outputType.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        outputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)outputRate);
        outputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        outputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)AacBytesPerSecond);
        return outputType;
    }

    private static IMFMediaType CreateAudioInputType(AudioInputFormat audio)
    {
        IMFMediaType inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
        inputType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audio.Channels);
        inputType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audio.SampleRate);
        inputType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 32u);
        inputType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)audio.BlockAlign);
        inputType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)audio.BytesPerSecond);
        inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        return inputType;
    }

    /// <summary>
    /// Applies rate-control mode, quality and GOP size to the encoder MFT through
    /// ICodecAPI. Every property is best-effort: vendor MFTs and the software fallback
    /// support different subsets, and an unsupported knob must degrade to encoder
    /// defaults rather than fail the recording. What was actually accepted is recorded
    /// in <see cref="AppliedEncoderProperties"/> for the session log.
    /// </summary>
    private void ApplyEncoderProperties(VideoEncodingConfig video)
    {
        IntPtr codecApiPointer;
        try
        {
            codecApiPointer = _writer.GetServiceForStream(_videoStreamIndex, Guid.Empty, typeof(CodecApi.ICodecAPI).GUID);
        }
        catch (SharpGenException)
        {
            AppliedEncoderProperties = "encoder defaults (ICodecAPI unavailable)";
            return;
        }

        var applied = new List<string>();
        try
        {
            var codecApi = (CodecApi.ICodecAPI)Marshal.GetObjectForIUnknown(codecApiPointer);

            uint? mode = video.RateControl switch
            {
                RateControlMode.Cbr => CodecApi.ModeCbr,
                RateControlMode.Vbr => CodecApi.ModePeakConstrainedVbr,
                RateControlMode.ConstantQuality => CodecApi.ModeQuality,
                _ => null,
            };
            if (mode is uint modeValue && CodecApi.TrySetUInt32(codecApi, CodecApi.RateControlMode, modeValue))
            {
                applied.Add($"rate-control={video.RateControl}");

                if (video.RateControl == RateControlMode.ConstantQuality &&
                    CodecApi.TrySetUInt32(codecApi, CodecApi.Quality, (uint)Math.Clamp(video.QualityLevel, 1, 100)))
                {
                    applied.Add($"quality={video.QualityLevel}");
                }
                if (video.RateControl is RateControlMode.Cbr or RateControlMode.Vbr &&
                    CodecApi.TrySetUInt32(codecApi, CodecApi.MeanBitRate, (uint)video.BitrateBitsPerSecond))
                {
                    applied.Add($"mean-bitrate={video.BitrateBitsPerSecond}");
                }
            }

            uint gopFrames = (uint)(video.KeyframeIntervalSeconds * video.FramesPerSecond);
            if (gopFrames > 0 && CodecApi.TrySetUInt32(codecApi, CodecApi.GopSize, gopFrames))
            {
                applied.Add($"gop={gopFrames}f");
            }
        }
        finally
        {
            Marshal.Release(codecApiPointer);
        }

        AppliedEncoderProperties = applied.Count > 0 ? string.Join(", ", applied) : "encoder defaults";
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
    /// Submits a chunk of float-PCM audio to the given track (index into the
    /// constructor's audioTracks). Bytes are copied into an MF memory buffer (audio
    /// volume is tiny next to video, so this copy is irrelevant to performance).
    /// </summary>
    public void WriteAudioChunk(int track, byte[] pcmData, int byteCount, long timestamp100Ns)
    {
        if (track < 0 || track >= _audioStreamIndices.Length || byteCount == 0)
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
        sample.SampleDuration = byteCount * 10_000_000L / _audioFormats[track].BytesPerSecond;
        _writer.WriteSample(_audioStreamIndices[track], sample);
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
        _fragmentedSink?.Dispose();
        _fragmentedByteStream?.Dispose();
        _deviceManager.Dispose();
        MediaFoundationRuntime.Release();
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
