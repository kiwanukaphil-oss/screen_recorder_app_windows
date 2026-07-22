using Recorder.Audio;
using Recorder.Capture;
using Recorder.Common.Buffers;
using Recorder.Common.Settings;
using Recorder.Common.Timing;
using Recorder.Encoding;
using Recorder.Graphics;
using Serilog;
using Vortice.Direct3D11;

namespace Recorder.Core;

/// <summary>
/// One recording from Start to finalized .mp4. Owns the thread model described in
/// PLAN.md §4.2:
///   - WGC capture thread produces GPU textures into a bounded drop-oldest queue;
///   - each audio source's capture thread produces PCM chunks into its own queue;
///   - one mux thread drains all queues and performs ALL sink-writer calls
///     (IMFSinkWriter is single-threaded by contract).
/// Every audio source becomes its own MP4 track (system audio = track 0, microphone
/// = track 1) so they can be mixed independently in editing.
/// All timestamps are QPC-derived and re-based so the recording starts at t = 0.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    // ~130 ms of video at 60 FPS. If the encoder falls this far behind, dropping the
    // oldest frame is the correct behavior — capture must never stall (PLAN principle 2).
    private const int VideoQueueCapacity = 8;

    /// <summary>
    /// Per-track audio state. The mux loop drives each track's clock (see
    /// KeepUpWithVideo): the 2026-07-22 soak crash proved that waiting for WASAPI
    /// delivery lets the sink writer buffer video without bound during silence.
    /// </summary>
    private sealed class AudioTrackPipeline
    {
        private const int QueueCapacity = 256;

        // Loopback audio stops flowing when nothing plays sound; gaps beyond this are
        // plugged with silence so the AAC track stays continuous and players keep sync.
        private const long GapThreshold100Ns = 500_000; // 50 ms

        // How far the synthesized-silence clock may trail the video clock. This bound
        // is what keeps sink-writer interleave buffering (and thus memory) flat.
        private const long MaxLagBehindVideo100Ns = 2_000_000; // 200 ms

        private readonly record struct PendingChunk(byte[] Pcm, long Timestamp100Ns);

        private readonly BoundedFrameQueue<PendingChunk> _queue = new(QueueCapacity);
        private long _nextTimestamp100Ns = -1;

        public required IAudioCaptureSource Source { get; init; }
        public required int TrackIndex { get; init; }

        /// <summary>Linear gain applied on the mux thread (1.0 = unity, 0 = mute).</summary>
        public required float Gain { get; init; }

        /// <summary>Capture thread: copy the transient buffer and queue it (never blocks).</summary>
        public void EnqueueChunk(byte[] pcm, int byteCount, long relativeTimestamp100Ns)
        {
            byte[] copy = new byte[byteCount];
            Buffer.BlockCopy(pcm, 0, copy, 0, byteCount);
            _queue.EnqueueDroppingOldest(new PendingChunk(copy, relativeTimestamp100Ns), out _);
        }

        /// <summary>
        /// Mux thread: write queued chunks on a continuous per-track timeline. Small
        /// jitter is absorbed by snapping to the timeline; a real gap (> 50 ms) is
        /// filled with explicit silence before the chunk so track duration always
        /// matches video.
        /// </summary>
        public void Drain(Mp4SinkWriter sink, RecordingStatistics statistics, long writeOffset100Ns)
        {
            while (_queue.TryDequeue(out PendingChunk chunk))
            {
                if (_nextTimestamp100Ns < 0)
                {
                    _nextTimestamp100Ns = chunk.Timestamp100Ns;
                }

                long gap = chunk.Timestamp100Ns - _nextTimestamp100Ns;
                if (gap > GapThreshold100Ns)
                {
                    WriteSilence(sink, gap, statistics, writeOffset100Ns);
                }

                ApplyGainInPlace(chunk.Pcm);
                sink.WriteAudioChunk(TrackIndex, chunk.Pcm, chunk.Pcm.Length, _nextTimestamp100Ns - writeOffset100Ns);
                _nextTimestamp100Ns += chunk.Pcm.Length * 10_000_000L / Source.BytesPerSecond;
                statistics.OnAudioChunkWritten();
            }
        }

        /// <summary>
        /// Mux thread: advance this track's clock toward the video clock with
        /// synthesized silence, independent of source delivery.
        /// </summary>
        public void KeepUpWithVideo(Mp4SinkWriter sink, long latestVideoTimestamp100Ns, RecordingStatistics statistics, long writeOffset100Ns)
        {
            long target = latestVideoTimestamp100Ns - MaxLagBehindVideo100Ns;
            if (_nextTimestamp100Ns < 0)
            {
                if (target <= 0)
                {
                    return;
                }
                _nextTimestamp100Ns = 0;
            }

            long gap = target - _nextTimestamp100Ns;
            if (gap > 0)
            {
                WriteSilence(sink, gap, statistics, writeOffset100Ns);
            }
        }

        public bool IsEmpty => _queue.Count == 0;

        /// <summary>
        /// Scales 32-bit float samples in place with clipping. Runs on the mux thread
        /// (not the capture callback) so a slow volume ramp can never delay capture.
        /// </summary>
        private void ApplyGainInPlace(byte[] pcm)
        {
            if (Math.Abs(Gain - 1f) < 0.001f)
            {
                return;
            }
            Span<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcm.AsSpan());
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Clamp(samples[i] * Gain, -1f, 1f);
            }
        }

        private void WriteSilence(Mp4SinkWriter sink, long duration100Ns, RecordingStatistics statistics, long writeOffset100Ns)
        {
            int silenceBytes = (int)(duration100Ns * Source.BytesPerSecond / 10_000_000L);
            silenceBytes -= silenceBytes % Source.BlockAlign();
            if (silenceBytes <= 0)
            {
                return;
            }
            sink.WriteAudioChunk(TrackIndex, new byte[silenceBytes], silenceBytes, _nextTimestamp100Ns - writeOffset100Ns);
            _nextTimestamp100Ns += silenceBytes * 10_000_000L / Source.BytesPerSecond;
            statistics.OnSilenceGapFilled();
        }
    }

    // Fail-safe: if private (commit) memory crosses this, something is stalling the
    // pipeline and the recording aborts cleanly instead of exhausting the pagefile.
    private const long PrivateMemoryCeilingBytes = 4L * 1024 * 1024 * 1024;
    private const long MemoryCheckInterval100Ns = 20_000_000; // 2 s

    private readonly record struct PendingVideoFrame(ID3D11Texture2D Texture, long Timestamp100Ns);

    private readonly ILogger _log;
    private readonly D3D11GraphicsDevice _graphicsDevice;
    private readonly ContinuousCaptureSource _captureSource;
    private readonly List<AudioTrackPipeline> _audioTracks = new();
    private readonly BoundedFrameQueue<PendingVideoFrame> _videoQueue = new(VideoQueueCapacity);
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly Thread _muxThread;
    private readonly FramePacer _framePacer;
    private readonly RecorderSettings _settings;
    private readonly VideoEncodingConfig _videoConfig;
    private readonly AudioInputFormat[] _sinkAudioFormats;
    private readonly bool _useFragmentedContainer;

    private Mp4SinkWriter _sinkWriter;
    private int _filePartNumber = 1;
    private long _splitOffset100Ns;
    private long _currentFileStart100Ns;

    private long _baseTimestamp100Ns;
    private long _latestVideoTimestamp100Ns;
    private long _lastResourceCheck100Ns;
    private long _pauseAdjustment100Ns;
    private long _pausedSince100Ns;
    private volatile bool _isPaused;
    private volatile bool _stopRequested;
    private Exception? _muxThreadFailure;

    public RecordingStatistics Statistics { get; } = new();
    public string OutputFilePath { get; }

    /// <summary>Every file this session produced (splitting creates several).</summary>
    public IReadOnlyList<string> OutputFiles => _outputFiles;
    private readonly List<string> _outputFiles = new();

    /// <summary>Non-null when the session stopped itself (e.g. disk low); UI should surface it.</summary>
    public string? AutoStopReason { get; private set; }

    public bool IsPaused => _isPaused;

    /// <summary>True once the mux thread has failed; the recording should be stopped promptly.</summary>
    public bool HasFailed => _muxThreadFailure is not null;

    /// <summary>
    /// QPC time (100 ns) of the recording's t = 0, set by Start(). Lets measurement
    /// tools (the A/V sync probe) convert their own QPC event times into positions on
    /// the recording's timeline.
    /// </summary>
    public long BaseTimestamp100Ns => _baseTimestamp100Ns;

    /// <summary>Records a monitor.</summary>
    public RecordingSession(
        D3D11GraphicsDevice graphicsDevice,
        RecorderSettings settings,
        MonitorInfo monitor,
        string outputFilePath,
        ILogger log,
        bool recordSystemAudio = true,
        bool recordMicrophone = false)
        : this(graphicsDevice, settings,
               ContinuousCaptureSource.ForMonitor(graphicsDevice, monitor, settings.CaptureCursor),
               outputFilePath, log, recordSystemAudio, recordMicrophone)
    {
    }

    /// <summary>Records a single window.</summary>
    public RecordingSession(
        D3D11GraphicsDevice graphicsDevice,
        RecorderSettings settings,
        CapturableWindow window,
        string outputFilePath,
        ILogger log,
        bool recordSystemAudio = true,
        bool recordMicrophone = false)
        : this(graphicsDevice, settings,
               ContinuousCaptureSource.ForWindow(graphicsDevice, window, settings.CaptureCursor),
               outputFilePath, log, recordSystemAudio, recordMicrophone)
    {
    }

    private RecordingSession(
        D3D11GraphicsDevice graphicsDevice,
        RecorderSettings settings,
        ContinuousCaptureSource captureSource,
        string outputFilePath,
        ILogger log,
        bool recordSystemAudio,
        bool recordMicrophone)
    {
        _log = log;
        _graphicsDevice = graphicsDevice;
        OutputFilePath = outputFilePath;
        _captureSource = captureSource;

        var sources = new List<(IAudioCaptureSource Source, float Gain)>();
        if (recordSystemAudio)
        {
            sources.Add((new SystemAudioLoopbackSource(), settings.SystemAudioVolumePercent / 100f));
        }
        if (recordMicrophone)
        {
            try
            {
                sources.Add((new MicrophoneCaptureSource(), settings.MicrophoneVolumePercent / 100f));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "No usable microphone; recording continues without a mic track");
            }
        }
        foreach (((IAudioCaptureSource source, float gain), int index) in sources.Select((s, i) => (s, i)))
        {
            _audioTracks.Add(new AudioTrackPipeline { Source = source, TrackIndex = index, Gain = gain });
        }

        VideoCodec codec = settings.Codec == VideoCodecPreference.Hevc ? VideoCodec.Hevc : VideoCodec.H264;
        RateControlMode rateControl = settings.RateControl.Trim().ToLowerInvariant() switch
        {
            "cbr" => RateControlMode.Cbr,
            "vbr" => RateControlMode.Vbr,
            "cq" => RateControlMode.ConstantQuality,
            _ => RateControlMode.Default,
        };
        int scalePercent = Math.Clamp(settings.OutputScalePercent, 25, 100);
        int outputWidth = VideoEncodingConfig.AlignEven(_captureSource.Width * scalePercent / 100);
        int outputHeight = VideoEncodingConfig.AlignEven(_captureSource.Height * scalePercent / 100);
        int bitrate = settings.VideoBitrateKbps is int kbps
            ? kbps * 1000
            : VideoEncodingConfig.AutoBitrate(outputWidth, outputHeight, settings.FramesPerSecond, codec);

        var videoConfig = new VideoEncodingConfig(
            outputWidth,
            outputHeight,
            settings.FramesPerSecond,
            bitrate,
            codec,
            rateControl,
            settings.QualityLevel,
            settings.KeyframeIntervalSeconds,
            SourceWidth: _captureSource.Width,
            SourceHeight: _captureSource.Height);

        _framePacer = new FramePacer(settings.FramesPerSecond);
        _settings = settings;
        _videoConfig = videoConfig;
        _sinkAudioFormats = _audioTracks.Select(t => new AudioInputFormat(t.Source.SampleRate, t.Source.Channels)).ToArray();

        _useFragmentedContainer = settings.CrashSafeContainer && _sinkAudioFormats.Length <= 1;
        if (settings.CrashSafeContainer && !_useFragmentedContainer)
        {
            _log.Warning("Crash-safe container disabled: fragmented MP4 supports one audio track " +
                         "and this session has {Tracks}; writing standard MP4", _sinkAudioFormats.Length);
        }

        _sinkWriter = new Mp4SinkWriter(
            outputFilePath, graphicsDevice.Device, videoConfig,
            blockOnEncoderBackpressure: false, _useFragmentedContainer, _sinkAudioFormats);
        _outputFiles.Add(outputFilePath);

        _muxThread = new Thread(RunMuxLoop)
        {
            Name = "RecorderMux",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };

        _log.Information(
            "Recording session ({Source}): {Width}x{Height}@{Fps} {Codec} ~{Mbps:0.#} Mbps [{EncoderProps}], audio tracks [{Audio}] -> {File}",
            _captureSource.SourceLabel,
            videoConfig.Width, videoConfig.Height, videoConfig.FramesPerSecond,
            videoConfig.Codec,
            videoConfig.BitrateBitsPerSecond / 1_000_000.0,
            _sinkWriter.AppliedEncoderProperties,
            string.Join("; ", _audioTracks.Select(t => $"{t.TrackIndex}: {t.Source.DisplayName} {t.Source.SampleRate} Hz x{t.Source.Channels}")),
            outputFilePath);
    }

    /// <summary>Starts the mux thread first, then all capture sources, anchoring t = 0 now.</summary>
    public void Start()
    {
        _baseTimestamp100Ns = QpcClock.GetTimestamp100Ns();
        _muxThread.Start();
        _captureSource.Start(HandleFrameReady);
        foreach (AudioTrackPipeline track in _audioTracks)
        {
            track.Source.Start((pcm, count, ts) => HandleAudioChunk(track, pcm, count, ts));
        }
    }

    /// <summary>
    /// Stops capture, drains whatever is still queued, finalizes the MP4 and surfaces
    /// any error the mux thread hit while recording. Called once; subsequent recordings
    /// use a fresh session instance.
    /// </summary>
    public void Stop()
    {
        _captureSource.Stop();
        foreach (AudioTrackPipeline track in _audioTracks)
        {
            track.Source.Stop();
        }

        _stopRequested = true;
        _workAvailable.Release();
        _muxThread.Join();

        _sinkWriter.FinalizeFile();

        if (_muxThreadFailure is not null)
        {
            throw new InvalidOperationException("Recording failed while writing samples.", _muxThreadFailure);
        }

        _log.Information(
            "Recording finished: WGC delivered {Delivered}, pacer rejected {Rejected}, queue dropped {Dropped}, " +
            "written {Frames}; audio chunks {Chunks}, silence gaps {Gaps}, source resizes {Resizes}",
            _captureSource.FramesDelivered, Statistics.FramesRejectedByPacer, Statistics.FramesDropped,
            Statistics.FramesWritten, Statistics.AudioChunksWritten, Statistics.SilenceGapsFilled,
            _captureSource.SourceResizes);
    }

    /// <summary>
    /// Pauses the recording: frames and audio are discarded and the paused wall time
    /// is later subtracted from every timestamp, so the output timeline is seamless —
    /// no frozen frames, no silence, no encoder restart.
    /// </summary>
    public void Pause()
    {
        if (_isPaused || _stopRequested)
        {
            return;
        }
        _pausedSince100Ns = QpcClock.GetTimestamp100Ns();
        _isPaused = true;
        _log.Information("Recording paused");
    }

    public void Resume()
    {
        if (!_isPaused)
        {
            return;
        }
        Interlocked.Add(ref _pauseAdjustment100Ns, QpcClock.GetTimestamp100Ns() - _pausedSince100Ns);
        _isPaused = false;
        _log.Information("Recording resumed");
    }

    /// <summary>
    /// WGC thread: re-base the timestamp (minus accumulated pause time), pace frames
    /// down to the configured FPS (see <see cref="FramePacer"/> for why over-delivery
    /// is dangerous), and queue the owned texture for the mux thread.
    /// </summary>
    private void HandleFrameReady(ID3D11Texture2D ownedTexture, long timestamp100Ns)
    {
        long relative = timestamp100Ns - _baseTimestamp100Ns - Interlocked.Read(ref _pauseAdjustment100Ns);
        if (relative < 0 || _stopRequested || _isPaused)
        {
            ownedTexture.Dispose();
            return;
        }
        if (!_framePacer.ShouldAccept(relative))
        {
            Statistics.OnFramePacerRejected();
            ownedTexture.Dispose();
            return;
        }

        if (_videoQueue.EnqueueDroppingOldest(new PendingVideoFrame(ownedTexture, relative), out PendingVideoFrame droppedFrame))
        {
            droppedFrame.Texture.Dispose();
            Statistics.OnFrameDropped();
        }
        _workAvailable.Release();
    }

    /// <summary>Audio capture thread: re-base (minus pause time) and hand to the track's queue.</summary>
    private void HandleAudioChunk(AudioTrackPipeline track, byte[] pcm, int byteCount, long chunkStart100Ns)
    {
        long relative = chunkStart100Ns - _baseTimestamp100Ns - Interlocked.Read(ref _pauseAdjustment100Ns);
        if (relative < 0 || _stopRequested || _isPaused || byteCount == 0)
        {
            return;
        }
        track.EnqueueChunk(pcm, byteCount, relative);
        _workAvailable.Release();
    }

    /// <summary>
    /// The single writer thread. Wakes when any queue has work, drains video first
    /// (the latency-critical stream), then each audio track, then advances every audio
    /// clock toward the video clock. Any sink-writer failure is captured and re-thrown
    /// from Stop(); producers keep feeding bounded queues meanwhile, so a
    /// mid-recording error never deadlocks them.
    /// </summary>
    private void RunMuxLoop()
    {
        try
        {
            while (true)
            {
                _workAvailable.Wait(TimeSpan.FromMilliseconds(250));

                while (_videoQueue.TryDequeue(out PendingVideoFrame frame))
                {
                    using ID3D11Texture2D texture = frame.Texture;
                    _sinkWriter.WriteVideoFrame(texture, frame.Timestamp100Ns - _splitOffset100Ns);
                    _latestVideoTimestamp100Ns = frame.Timestamp100Ns;
                    Statistics.OnFrameWritten();
                }

                foreach (AudioTrackPipeline track in _audioTracks)
                {
                    track.Drain(_sinkWriter, Statistics, _splitOffset100Ns);
                    track.KeepUpWithVideo(_sinkWriter, _latestVideoTimestamp100Ns, Statistics, _splitOffset100Ns);
                }

                RotateOutputFileIfDue();
                EnforceResourceLimits();

                if (_stopRequested && _videoQueue.Count == 0 && _audioTracks.All(t => t.IsEmpty))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _muxThreadFailure = ex;
            _log.Error(ex, "Mux thread failed; recording will stop");
        }
    }

    /// <summary>
    /// Mux thread: closes the current file and opens the next part when the configured
    /// duration or size limit is reached. Timestamps are re-based so each part starts
    /// at t = 0; the audio pipelines keep their global clocks and only the write
    /// offset changes, so cross-part A/V alignment is preserved.
    /// </summary>
    private void RotateOutputFileIfDue()
    {
        bool durationDue = _settings.SplitMaxDurationSeconds is int maxSeconds &&
            _latestVideoTimestamp100Ns - _currentFileStart100Ns >= maxSeconds * 10_000_000L;
        bool sizeDue = false;
        if (!durationDue && _settings.SplitMaxSizeMb is int maxMb)
        {
            var info = new FileInfo(_outputFiles[^1]);
            sizeDue = info.Exists && info.Length >= (long)maxMb * 1024 * 1024;
        }
        if (!durationDue && !sizeDue)
        {
            return;
        }

        _sinkWriter.FinalizeFile();
        _sinkWriter.Dispose();

        _filePartNumber++;
        string nextPath = Path.Combine(
            Path.GetDirectoryName(OutputFilePath)!,
            $"{Path.GetFileNameWithoutExtension(OutputFilePath)}-part{_filePartNumber}{Path.GetExtension(OutputFilePath)}");

        _splitOffset100Ns = _latestVideoTimestamp100Ns + 10_000_000L / _videoConfig.FramesPerSecond;
        _currentFileStart100Ns = _splitOffset100Ns;
        _sinkWriter = new Mp4SinkWriter(
            nextPath, _graphicsDevice.Device, _videoConfig,
            blockOnEncoderBackpressure: false, _useFragmentedContainer, _sinkAudioFormats);
        _outputFiles.Add(nextPath);
        _log.Information("Split: continuing recording in {File}", nextPath);
    }

    /// <summary>
    /// Cheap watchdog (checked every ~2 s). Memory over the ceiling aborts with an
    /// error (a stalled pipeline — bounds the known failure mode while the mux loop is
    /// making progress; it cannot fire while blocked inside a native call). Low disk
    /// stops GRACEFULLY: the current file finalizes cleanly and AutoStopReason tells
    /// the UI why.
    /// </summary>
    private void EnforceResourceLimits()
    {
        long now = QpcClock.GetTimestamp100Ns();
        if (now - _lastResourceCheck100Ns < MemoryCheckInterval100Ns)
        {
            return;
        }
        _lastResourceCheck100Ns = now;

        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        long privateBytes = currentProcess.PrivateMemorySize64;
        if (privateBytes > PrivateMemoryCeilingBytes)
        {
            throw new InvalidOperationException(
                $"Recording aborted by memory fail-safe: private memory {privateBytes / (1024 * 1024)} MB " +
                "indicates a stalled encoding pipeline.");
        }

        var drive = new DriveInfo(Path.GetPathRoot(OutputFilePath)!);
        if (drive.AvailableFreeSpace < (long)_settings.MinFreeDiskGb * 1024 * 1024 * 1024)
        {
            AutoStopReason = $"Free disk space fell below {_settings.MinFreeDiskGb} GB";
            _log.Warning("{Reason}; stopping recording gracefully", AutoStopReason);
            _stopRequested = true;
        }
    }

    public void Dispose()
    {
        _captureSource.Dispose();
        foreach (AudioTrackPipeline track in _audioTracks)
        {
            track.Source.Dispose();
        }

        while (_videoQueue.TryDequeue(out PendingVideoFrame leftover))
        {
            leftover.Texture.Dispose();
        }

        _sinkWriter.Dispose();
        _workAvailable.Dispose();
    }
}

file static class AudioSourceExtensions
{
    public static int BlockAlign(this IAudioCaptureSource source) => source.Channels * 4;
}
