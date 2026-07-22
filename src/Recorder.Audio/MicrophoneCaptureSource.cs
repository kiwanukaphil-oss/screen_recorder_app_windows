using NAudio.CoreAudioApi;
using NAudio.Wave;
using Recorder.Common.Timing;

namespace Recorder.Audio;

/// <summary>
/// Captures the default microphone via WASAPI (shared mode, event-driven) as 32-bit
/// float in the device mix format. Unlike loopback, a live microphone delivers data
/// continuously (room noise is still data), but the session applies the same
/// gap-fill safety net regardless.
/// </summary>
public sealed class MicrophoneCaptureSource : IAudioCaptureSource
{
    private readonly WasapiCapture _capture;
    private AudioChunkCallback? _onChunk;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BytesPerSecond { get; }
    public string DisplayName { get; }

    public MicrophoneCaptureSource()
    {
        _capture = new WasapiCapture();
        _capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            _capture.WaveFormat.SampleRate, _capture.WaveFormat.Channels);
        SampleRate = _capture.WaveFormat.SampleRate;
        Channels = _capture.WaveFormat.Channels;
        BytesPerSecond = _capture.WaveFormat.AverageBytesPerSecond;

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        DisplayName = device.FriendlyName;
    }

    /// <summary>Chunk start time = arrival minus the chunk's own duration (same rule as loopback).</summary>
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
        if (_capture.CaptureState is not CaptureState.Stopped)
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
