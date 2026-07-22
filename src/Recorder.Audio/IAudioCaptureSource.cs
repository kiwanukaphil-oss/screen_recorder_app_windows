namespace Recorder.Audio;

/// <summary>
/// Receives raw PCM bytes plus the QPC time (100 ns) at which the FIRST sample of the
/// chunk was captured. Runs on the source's capture thread; the byte array is only
/// valid during the callback and must be copied by the receiver.
/// </summary>
public delegate void AudioChunkCallback(byte[] pcmData, int byteCount, long chunkStartTimestamp100Ns);

/// <summary>
/// A live PCM source (system loopback, microphone, later per-app capture). All
/// implementations deliver 32-bit float in the device mix format and stamp chunks
/// from the shared QPC clock so tracks from different sources stay aligned.
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    int BytesPerSecond { get; }

    /// <summary>Human-readable name for logs and (later) the mixer UI.</summary>
    string DisplayName { get; }

    void Start(AudioChunkCallback onChunk);
    void Stop();
}
