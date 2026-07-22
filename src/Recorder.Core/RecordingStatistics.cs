namespace Recorder.Core;

/// <summary>Live counters for the diagnostics view and end-of-recording summary.</summary>
public sealed class RecordingStatistics
{
    private long _framesWritten;
    private long _framesDropped;
    private long _framesRejectedByPacer;
    private long _audioChunksWritten;
    private long _silenceGapsFilled;

    /// <summary>Samples submitted to the sink writer.</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>Frames evicted because the bounded video queue overflowed (encoder too slow).</summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Frames intentionally decimated by <c>FramePacer</c> to honor the target FPS (not a fault).</summary>
    public long FramesRejectedByPacer => Interlocked.Read(ref _framesRejectedByPacer);

    public long AudioChunksWritten => Interlocked.Read(ref _audioChunksWritten);
    public long SilenceGapsFilled => Interlocked.Read(ref _silenceGapsFilled);

    internal void OnFrameWritten() => Interlocked.Increment(ref _framesWritten);
    internal void OnFrameDropped() => Interlocked.Increment(ref _framesDropped);
    internal void OnFramePacerRejected() => Interlocked.Increment(ref _framesRejectedByPacer);
    internal void OnAudioChunkWritten() => Interlocked.Increment(ref _audioChunksWritten);
    internal void OnSilenceGapFilled() => Interlocked.Increment(ref _silenceGapsFilled);
}
