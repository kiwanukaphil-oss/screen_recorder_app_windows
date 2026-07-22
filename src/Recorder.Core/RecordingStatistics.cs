namespace Recorder.Core;

/// <summary>Live counters for the diagnostics view and end-of-recording summary.</summary>
public sealed class RecordingStatistics
{
    private long _framesWritten;
    private long _framesDropped;
    private long _audioChunksWritten;
    private long _silenceGapsFilled;

    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public long AudioChunksWritten => Interlocked.Read(ref _audioChunksWritten);
    public long SilenceGapsFilled => Interlocked.Read(ref _silenceGapsFilled);

    internal void OnFrameWritten() => Interlocked.Increment(ref _framesWritten);
    internal void OnFrameDropped() => Interlocked.Increment(ref _framesDropped);
    internal void OnAudioChunkWritten() => Interlocked.Increment(ref _audioChunksWritten);
    internal void OnSilenceGapFilled() => Interlocked.Increment(ref _silenceGapsFilled);
}
