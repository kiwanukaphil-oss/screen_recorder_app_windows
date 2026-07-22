using NAudio.CoreAudioApi;
using NAudio.Wave;
using Recorder.Common.Timing;

namespace Recorder.SyncProbe;

/// <summary>
/// Continuously renders silence to the default output through a low-latency shared
/// WASAPI stream; FireBeep() splices in a 1 kHz tone and returns the QPC instant of
/// the request. Keeping the stream always running means a beep's submission-to-mix
/// delay is bounded by the 20 ms buffer rather than by stream startup (~100+ ms),
/// which is what makes the probe's audio event times trustworthy.
/// </summary>
public sealed class BeepPlayer : IDisposable
{
    private sealed class SilenceOrBeepProvider : ISampleProvider
    {
        private const double Frequency = 1000;
        private int _beepSamplesRemaining;
        private double _phase;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public void StartBeep(int durationMs)
            => Interlocked.Exchange(ref _beepSamplesRemaining, WaveFormat.SampleRate * durationMs / 1000);

        /// <summary>
        /// Fills the buffer with silence, or with a 0.3-amplitude sine while a beep is
        /// active. Interleaved stereo: the same sample value goes to both channels and
        /// the per-frame counter only decrements once per frame.
        /// </summary>
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i += 2)
            {
                float value = 0f;
                if (_beepSamplesRemaining > 0)
                {
                    value = (float)(0.3 * Math.Sin(_phase));
                    _phase += 2 * Math.PI * Frequency / WaveFormat.SampleRate;
                    _beepSamplesRemaining--;
                }
                buffer[offset + i] = value;
                if (i + 1 < count)
                {
                    buffer[offset + i + 1] = value;
                }
            }
            return count;
        }
    }

    private readonly WasapiOut _output;
    private readonly SilenceOrBeepProvider _provider = new();

    public BeepPlayer()
    {
        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 20);
        _output.Init(_provider);
        _output.Play();
    }

    /// <summary>Requests a beep and returns the QPC time (100 ns) of the request.</summary>
    public long FireBeep(int durationMs)
    {
        long timestamp = QpcClock.GetTimestamp100Ns();
        _provider.StartBeep(durationMs);
        return timestamp;
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}
