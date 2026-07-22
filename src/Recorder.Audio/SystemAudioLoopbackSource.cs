using NAudio.Wave;
using Recorder.Common.Timing;

namespace Recorder.Audio;

/// <summary>
/// Captures everything the default render device plays (WASAPI loopback) as 32-bit
/// float PCM in the device's mix format. NAudio is used for M1 pragmatism; if M2's
/// sync measurements demand tighter timestamp control, this is the one class to
/// replace with direct WASAPI interop — its callback contract already matches.
/// </summary>
public sealed class SystemAudioLoopbackSource : IDisposable
{
    /// <summary>
    /// Receives raw PCM bytes plus the QPC time (100 ns) at which the FIRST sample of
    /// the chunk was captured. Runs on NAudio's capture thread; the byte array is only
    /// valid during the callback and must be copied by the receiver.
    /// </summary>
    public delegate void AudioChunkCallback(byte[] pcmData, int byteCount, long chunkStartTimestamp100Ns);

    private readonly WasapiLoopbackCapture _capture;
    private AudioChunkCallback? _onChunk;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BytesPerSecond { get; }

    public SystemAudioLoopbackSource()
    {
        _capture = new WasapiLoopbackCapture();
        SampleRate = _capture.WaveFormat.SampleRate;
        Channels = _capture.WaveFormat.Channels;
        BytesPerSecond = _capture.WaveFormat.AverageBytesPerSecond;
    }

    /// <summary>
    /// Starts loopback capture. The chunk's start time is derived by subtracting the
    /// chunk's own duration from the arrival time — WASAPI delivered these samples
    /// because they just finished playing, so arrival ≈ end of chunk.
    /// </summary>
    public void Start(AudioChunkCallback onChunk)
    {
        _onChunk = onChunk;
        _capture.DataAvailable += (_, args) =>
        {
            long arrival100Ns = QpcClock.GetTimestamp100Ns();
            long chunkDuration100Ns = args.BytesRecorded * 10_000_000L / BytesPerSecond;
            _onChunk?.Invoke(args.Buffer, args.BytesRecorded, arrival100Ns - chunkDuration100Ns);
        };
        _capture.StartRecording();
    }

    public void Stop()
    {
        _onChunk = null;
        if (_capture.CaptureState is not NAudio.CoreAudioApi.CaptureState.Stopped)
        {
            _capture.StopRecording();
        }
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }
}
