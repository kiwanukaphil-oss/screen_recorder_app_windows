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
///   - NAudio capture thread produces PCM chunks into a second bounded queue;
///   - one mux thread drains both queues and performs ALL sink-writer calls
///     (IMFSinkWriter is single-threaded by contract).
/// All timestamps are QPC-derived and re-based so the recording starts at t = 0.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    // ~130 ms of video at 60 FPS. If the encoder falls this far behind, dropping the
    // oldest frame is the correct behavior — capture must never stall (PLAN principle 2).
    private const int VideoQueueCapacity = 8;
    private const int AudioQueueCapacity = 256;

    // Loopback audio stops flowing when nothing plays sound; gaps beyond this are
    // plugged with silence so the AAC track stays continuous and players keep sync.
    private const long AudioGapThreshold100Ns = 500_000; // 50 ms

    // How far the synthesized-silence audio clock is allowed to trail the video clock.
    // The sink writer buffers video in RAM while audio lags (it must interleave the
    // streams), so this bound is what keeps memory flat when the system is silent —
    // an unbounded lag once ballooned commit memory to 29 GB and took the machine down.
    private const long MaxAudioLagBehindVideo100Ns = 2_000_000; // 200 ms

    // Fail-safe: if private (commit) memory crosses this, something is stalling the
    // pipeline and the recording aborts cleanly instead of exhausting the pagefile.
    private const long PrivateMemoryCeilingBytes = 4L * 1024 * 1024 * 1024;
    private const long MemoryCheckInterval100Ns = 20_000_000; // 2 s

    private readonly record struct PendingVideoFrame(ID3D11Texture2D Texture, long Timestamp100Ns);
    private readonly record struct PendingAudioChunk(byte[] Pcm, long Timestamp100Ns);

    private readonly ILogger _log;
    private readonly D3D11GraphicsDevice _graphicsDevice;
    private readonly ContinuousMonitorCaptureSource _captureSource;
    private readonly SystemAudioLoopbackSource? _audioSource;
    private readonly Mp4SinkWriter _sinkWriter;
    private readonly BoundedFrameQueue<PendingVideoFrame> _videoQueue = new(VideoQueueCapacity);
    private readonly BoundedFrameQueue<PendingAudioChunk> _audioQueue = new(AudioQueueCapacity);
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly Thread _muxThread;

    private readonly FramePacer _framePacer;
    private long _baseTimestamp100Ns;
    private long _nextAudioTimestamp100Ns = -1;
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
        bool recordSystemAudio = true)
    {
        _log = log;
        _graphicsDevice = graphicsDevice;
        OutputFilePath = outputFilePath;

        _captureSource = new ContinuousMonitorCaptureSource(graphicsDevice, monitor, settings.CaptureCursor);

        AudioInputFormat? audioFormat = null;
        if (recordSystemAudio)
        {
            _audioSource = new SystemAudioLoopbackSource();
            audioFormat = new AudioInputFormat(_audioSource.SampleRate, _audioSource.Channels);
        }

        var videoConfig = new VideoEncodingConfig(
            _captureSource.Width,
            _captureSource.Height,
            settings.FramesPerSecond,
            VideoEncodingConfig.AutoBitrate(_captureSource.Width, _captureSource.Height, settings.FramesPerSecond));

        _framePacer = new FramePacer(settings.FramesPerSecond);
        _sinkWriter = new Mp4SinkWriter(outputFilePath, graphicsDevice.Device, videoConfig, audioFormat);
        _muxThread = new Thread(RunMuxLoop)
        {
            Name = "RecorderMux",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };

        _log.Information(
            "Recording session: {Width}x{Height}@{Fps} ~{Mbps:0.#} Mbps, audio {Audio} -> {File}",
            videoConfig.Width, videoConfig.Height, videoConfig.FramesPerSecond,
            videoConfig.BitrateBitsPerSecond / 1_000_000.0,
            audioFormat is null ? "off" : $"{audioFormat.SampleRate} Hz x{audioFormat.Channels}",
            outputFilePath);
    }

    /// <summary>Starts the mux thread first, then both capture sources, anchoring t = 0 now.</summary>
    public void Start()
    {
        _baseTimestamp100Ns = QpcClock.GetTimestamp100Ns();
        _muxThread.Start();
        _captureSource.Start(HandleFrameReady);
        _audioSource?.Start(HandleAudioChunk);
    }

    /// <summary>
    /// Stops capture, drains whatever is still queued, finalizes the MP4 and surfaces
    /// any error the mux thread hit while recording. Called once; subsequent recordings
    /// use a fresh session instance.
    /// </summary>
    public void Stop()
    {
        _captureSource.Stop();
        _audioSource?.Stop();

        _stopRequested = true;
        _workAvailable.Release();
        _muxThread.Join();

        _sinkWriter.FinalizeFile();

        if (_muxThreadFailure is not null)
        {
            throw new InvalidOperationException("Recording failed while writing samples.", _muxThreadFailure);
        }

        _log.Information(
            "Recording finished: {Frames} frames written, {Dropped} dropped, {Chunks} audio chunks, {Gaps} silence gaps filled",
            Statistics.FramesWritten, Statistics.FramesDropped,
            Statistics.AudioChunksWritten, Statistics.SilenceGapsFilled);
    }

    /// <summary>
    /// WGC thread: re-base the timestamp, pace frames down to the configured FPS
    /// (see <see cref="FramePacer"/> for why over-delivery is dangerous), and queue
    /// the owned texture for the mux thread.
    /// </summary>
    private void HandleFrameReady(ID3D11Texture2D ownedTexture, long timestamp100Ns)
    {
        long relative = timestamp100Ns - _baseTimestamp100Ns;
        if (relative < 0 || _stopRequested || !_framePacer.ShouldAccept(relative))
        {
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

    /// <summary>NAudio thread: copy the transient buffer, re-base, queue for the mux thread.</summary>
    private void HandleAudioChunk(byte[] pcm, int byteCount, long chunkStart100Ns)
    {
        long relative = chunkStart100Ns - _baseTimestamp100Ns;
        if (relative < 0 || _stopRequested || byteCount == 0)
        {
            return;
        }

        byte[] copy = new byte[byteCount];
        Buffer.BlockCopy(pcm, 0, copy, 0, byteCount);
        _audioQueue.EnqueueDroppingOldest(new PendingAudioChunk(copy, relative), out _);
        _workAvailable.Release();
    }

    /// <summary>
    /// The single writer thread. Wakes when either queue has work, drains video first
    /// (the latency-critical stream), then audio. Any sink-writer failure is captured
    /// and re-thrown from Stop(): the capture callbacks keep queueing into bounded
    /// queues meanwhile, so a mid-recording error never deadlocks producers.
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

                while (_audioQueue.TryDequeue(out PendingAudioChunk chunk))
                {
                    WriteAudioWithGapFill(chunk);
                }

                KeepAudioClockUpWithVideo();
                EnforceMemoryCeiling();

                if (_stopRequested && _videoQueue.Count == 0 && _audioQueue.Count == 0)
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
    /// Writes an audio chunk on a continuous timeline. WASAPI loopback can go quiet
    /// when no application is rendering sound; the resulting hole is filled with
    /// explicit silence so the audio track's duration always matches the video's.
    /// Small timing jitter (chunk start vs. expected next sample) is absorbed by
    /// snapping to the continuous timeline; a large gap (> 50 ms) is treated as real
    /// silence; a chunk arriving before its slot is written at the timeline position.
    /// </summary>
    private void WriteAudioWithGapFill(PendingAudioChunk chunk)
    {
        int bytesPerSecond = _audioSource!.BytesPerSecond;
        int blockAlign = _audioSource.Channels * 4;

        if (_nextAudioTimestamp100Ns < 0)
        {
            _nextAudioTimestamp100Ns = chunk.Timestamp100Ns;
        }

        long gap = chunk.Timestamp100Ns - _nextAudioTimestamp100Ns;
        if (gap > AudioGapThreshold100Ns)
        {
            int silenceBytes = (int)(gap * bytesPerSecond / 10_000_000L);
            silenceBytes -= silenceBytes % blockAlign;
            if (silenceBytes > 0)
            {
                _sinkWriter.WriteAudioChunk(new byte[silenceBytes], silenceBytes, _nextAudioTimestamp100Ns);
                _nextAudioTimestamp100Ns += silenceBytes * 10_000_000L / bytesPerSecond;
                Statistics.OnSilenceGapFilled();
            }
        }

        _sinkWriter.WriteAudioChunk(chunk.Pcm, chunk.Pcm.Length, _nextAudioTimestamp100Ns);
        _nextAudioTimestamp100Ns += chunk.Pcm.Length * 10_000_000L / bytesPerSecond;
        Statistics.OnAudioChunkWritten();
    }

    /// <summary>
    /// THE lesson of the first soak test: WASAPI loopback delivers NOTHING while the
    /// system is silent, and if the audio stream's clock stands still the sink writer
    /// buffers every incoming raw 4K frame in RAM waiting to interleave the streams
    /// (~100 MB/s until the machine dies). So the audio clock is driven from the mux
    /// loop itself: whenever written audio trails written video by more than the
    /// allowed lag, the difference is filled with synthesized silence — chunk arrival
    /// is no longer required for the audio timeline to advance.
    /// </summary>
    private void KeepAudioClockUpWithVideo()
    {
        if (_audioSource is null)
        {
            return;
        }

        long target = _latestVideoTimestamp100Ns - MaxAudioLagBehindVideo100Ns;
        if (_nextAudioTimestamp100Ns < 0)
        {
            if (target <= 0)
            {
                return;
            }
            _nextAudioTimestamp100Ns = 0;
        }

        long gap = target - _nextAudioTimestamp100Ns;
        if (gap <= 0)
        {
            return;
        }

        int bytesPerSecond = _audioSource.BytesPerSecond;
        int silenceBytes = (int)(gap * bytesPerSecond / 10_000_000L);
        silenceBytes -= silenceBytes % (_audioSource.Channels * 4);
        if (silenceBytes <= 0)
        {
            return;
        }

        _sinkWriter.WriteAudioChunk(new byte[silenceBytes], silenceBytes, _nextAudioTimestamp100Ns);
        _nextAudioTimestamp100Ns += silenceBytes * 10_000_000L / bytesPerSecond;
        Statistics.OnSilenceGapFilled();
    }

    /// <summary>
    /// Cheap watchdog (checked every ~2 s of media time): if commit memory crosses the
    /// ceiling, some stage is hoarding samples — abort the recording with a clear error
    /// instead of exhausting the pagefile and destabilizing the whole machine.
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
        _audioSource?.Dispose();

        while (_videoQueue.TryDequeue(out PendingVideoFrame leftover))
        {
            leftover.Texture.Dispose();
        }

        _sinkWriter.Dispose();
        _workAvailable.Dispose();
    }
}
