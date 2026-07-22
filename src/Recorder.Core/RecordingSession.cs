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
        public void Drain(Mp4SinkWriter sink, RecordingStatistics statistics)
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
                    WriteSilence(sink, gap, statistics);
                }

                sink.WriteAudioChunk(TrackIndex, chunk.Pcm, chunk.Pcm.Length, _nextTimestamp100Ns);
                _nextTimestamp100Ns += chunk.Pcm.Length * 10_000_000L / Source.BytesPerSecond;
                statistics.OnAudioChunkWritten();
            }
        }

        /// <summary>
        /// Mux thread: advance this track's clock toward the video clock with
        /// synthesized silence, independent of source delivery.
        /// </summary>
        public void KeepUpWithVideo(Mp4SinkWriter sink, long latestVideoTimestamp100Ns, RecordingStatistics statistics)
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
                WriteSilence(sink, gap, statistics);
            }
        }

        public bool IsEmpty => _queue.Count == 0;

        private void WriteSilence(Mp4SinkWriter sink, long duration100Ns, RecordingStatistics statistics)
        {
            int silenceBytes = (int)(duration100Ns * Source.BytesPerSecond / 10_000_000L);
            silenceBytes -= silenceBytes % Source.BlockAlign();
            if (silenceBytes <= 0)
            {
                return;
            }
            sink.WriteAudioChunk(TrackIndex, new byte[silenceBytes], silenceBytes, _nextTimestamp100Ns);
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
    private readonly ContinuousMonitorCaptureSource _captureSource;
    private readonly List<AudioTrackPipeline> _audioTracks = new();
    private readonly Mp4SinkWriter _sinkWriter;
    private readonly BoundedFrameQueue<PendingVideoFrame> _videoQueue = new(VideoQueueCapacity);
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly Thread _muxThread;
    private readonly FramePacer _framePacer;

    private long _baseTimestamp100Ns;
    private long _latestVideoTimestamp100Ns;
    private long _lastMemoryCheck100Ns;
    private volatile bool _stopRequested;
    private Exception? _muxThreadFailure;

    public RecordingStatistics Statistics { get; } = new();
    public string OutputFilePath { get; }

    /// <summary>True once the mux thread has failed; the recording should be stopped promptly.</summary>
    public bool HasFailed => _muxThreadFailure is not null;

    public RecordingSession(
        D3D11GraphicsDevice graphicsDevice,
        RecorderSettings settings,
        MonitorInfo monitor,
        string outputFilePath,
        ILogger log,
        bool recordSystemAudio = true,
        bool recordMicrophone = false)
    {
        _log = log;
        _graphicsDevice = graphicsDevice;
        OutputFilePath = outputFilePath;

        _captureSource = new ContinuousMonitorCaptureSource(graphicsDevice, monitor, settings.CaptureCursor);

        var sources = new List<IAudioCaptureSource>();
        if (recordSystemAudio)
        {
            sources.Add(new SystemAudioLoopbackSource());
        }
        if (recordMicrophone)
        {
            try
            {
                sources.Add(new MicrophoneCaptureSource());
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "No usable microphone; recording continues without a mic track");
            }
        }
        foreach ((IAudioCaptureSource source, int index) in sources.Select((s, i) => (s, i)))
        {
            _audioTracks.Add(new AudioTrackPipeline { Source = source, TrackIndex = index });
        }

        VideoCodec codec = settings.Codec == VideoCodecPreference.Hevc ? VideoCodec.Hevc : VideoCodec.H264;
        RateControlMode rateControl = settings.RateControl.Trim().ToLowerInvariant() switch
        {
            "cbr" => RateControlMode.Cbr,
            "vbr" => RateControlMode.Vbr,
            "cq" => RateControlMode.ConstantQuality,
            _ => RateControlMode.Default,
        };
        int bitrate = settings.VideoBitrateKbps is int kbps
            ? kbps * 1000
            : VideoEncodingConfig.AutoBitrate(_captureSource.Width, _captureSource.Height, settings.FramesPerSecond, codec);

        var videoConfig = new VideoEncodingConfig(
            _captureSource.Width,
            _captureSource.Height,
            settings.FramesPerSecond,
            bitrate,
            codec,
            rateControl,
            settings.QualityLevel,
            settings.KeyframeIntervalSeconds);

        _framePacer = new FramePacer(settings.FramesPerSecond);
        _sinkWriter = new Mp4SinkWriter(
            outputFilePath,
            graphicsDevice.Device,
            videoConfig,
            _audioTracks.Select(t => new AudioInputFormat(t.Source.SampleRate, t.Source.Channels)).ToArray());

        _muxThread = new Thread(RunMuxLoop)
        {
            Name = "RecorderMux",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };

        _log.Information(
            "Recording session: {Width}x{Height}@{Fps} {Codec} ~{Mbps:0.#} Mbps [{EncoderProps}], audio tracks [{Audio}] -> {File}",
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
            "written {Frames}; audio chunks {Chunks}, silence gaps {Gaps}",
            _captureSource.FramesDelivered, Statistics.FramesRejectedByPacer, Statistics.FramesDropped,
            Statistics.FramesWritten, Statistics.AudioChunksWritten, Statistics.SilenceGapsFilled);
    }

    /// <summary>
    /// WGC thread: re-base the timestamp, pace frames down to the configured FPS
    /// (see <see cref="FramePacer"/> for why over-delivery is dangerous), and queue
    /// the owned texture for the mux thread.
    /// </summary>
    private void HandleFrameReady(ID3D11Texture2D ownedTexture, long timestamp100Ns)
    {
        long relative = timestamp100Ns - _baseTimestamp100Ns;
        if (relative < 0 || _stopRequested)
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

    /// <summary>Audio capture thread: re-base and hand to the track's own queue.</summary>
    private void HandleAudioChunk(AudioTrackPipeline track, byte[] pcm, int byteCount, long chunkStart100Ns)
    {
        long relative = chunkStart100Ns - _baseTimestamp100Ns;
        if (relative < 0 || _stopRequested || byteCount == 0)
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
                    _sinkWriter.WriteVideoFrame(texture, frame.Timestamp100Ns);
                    _latestVideoTimestamp100Ns = frame.Timestamp100Ns;
                    Statistics.OnFrameWritten();
                }

                foreach (AudioTrackPipeline track in _audioTracks)
                {
                    track.Drain(_sinkWriter, Statistics);
                    track.KeepUpWithVideo(_sinkWriter, _latestVideoTimestamp100Ns, Statistics);
                }

                EnforceMemoryCeiling();

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
    /// Cheap watchdog (checked every ~2 s): if commit memory crosses the ceiling, some
    /// stage is hoarding samples — abort the recording with a clear error instead of
    /// exhausting the pagefile. This bounds the known failure mode while the mux loop
    /// is making progress; it cannot fire while blocked inside a native call.
    /// </summary>
    private void EnforceMemoryCeiling()
    {
        long now = QpcClock.GetTimestamp100Ns();
        if (now - _lastMemoryCheck100Ns < MemoryCheckInterval100Ns)
        {
            return;
        }
        _lastMemoryCheck100Ns = now;

        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        long privateBytes = currentProcess.PrivateMemorySize64;
        if (privateBytes > PrivateMemoryCeilingBytes)
        {
            throw new InvalidOperationException(
                $"Recording aborted by memory fail-safe: private memory {privateBytes / (1024 * 1024)} MB " +
                "indicates a stalled encoding pipeline.");
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
