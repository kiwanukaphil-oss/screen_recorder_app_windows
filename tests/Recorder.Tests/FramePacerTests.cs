using Recorder.Common.Timing;

namespace Recorder.Tests;

public class FramePacerTests
{
    private const long Tick100NsPerSecond = 10_000_000;

    private static long At60Hz(int frameIndex) => frameIndex * Tick100NsPerSecond / 60;

    [Fact]
    public void Keeps_every_frame_when_delivery_matches_target()
    {
        var pacer = new FramePacer(60);
        int accepted = Enumerable.Range(0, 120).Count(i => pacer.ShouldAccept(At60Hz(i)));
        Assert.Equal(120, accepted);
    }

    [Fact]
    public void Halves_60hz_delivery_for_a_30fps_target()
    {
        var pacer = new FramePacer(30);
        int accepted = Enumerable.Range(0, 120).Count(i => pacer.ShouldAccept(At60Hz(i)));

        // 120 frames over 2 s of 60 Hz input -> 30 fps target keeps 60 (+-1 for phase).
        Assert.InRange(accepted, 59, 61);
    }

    [Fact]
    public void Accepted_frames_stay_evenly_spaced_under_bursty_delivery()
    {
        var pacer = new FramePacer(30);
        var acceptedTimes = new List<long>();

        // Bursty 60 Hz delivery: frames sometimes jittered 4 ms late.
        for (int i = 0; i < 300; i++)
        {
            long jitter = i % 3 == 0 ? 40_000 : 0;
            long time = At60Hz(i) + jitter;
            if (pacer.ShouldAccept(time))
            {
                acceptedTimes.Add(time);
            }
        }

        long targetInterval = Tick100NsPerSecond / 30;
        for (int i = 1; i < acceptedTimes.Count; i++)
        {
            long delta = acceptedTimes[i] - acceptedTimes[i - 1];
            Assert.InRange(delta, targetInterval / 2, targetInterval * 2);
        }
    }

    [Fact]
    public void Resumes_promptly_after_idle_gap_without_double_accepting()
    {
        var pacer = new FramePacer(30);
        Assert.True(pacer.ShouldAccept(0));

        // Screen idle for 5 s (no frames), then two frames arrive 16 ms apart.
        long resume = 5 * Tick100NsPerSecond;
        Assert.True(pacer.ShouldAccept(resume));
        Assert.False(pacer.ShouldAccept(resume + 160_000));
    }

    [Fact]
    public void Rejects_invalid_target_rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(0));
    }
}
