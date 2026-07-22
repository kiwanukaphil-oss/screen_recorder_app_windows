using Recorder.Common.Timing;

namespace Recorder.Tests;

public class QpcClockTests
{
    [Fact]
    public void Timestamps_never_decrease()
    {
        long previous = QpcClock.GetTimestamp100Ns();
        for (int i = 0; i < 10_000; i++)
        {
            long current = QpcClock.GetTimestamp100Ns();
            Assert.True(current >= previous, $"Clock went backwards: {previous} -> {current}");
            previous = current;
        }
    }

    [Fact]
    public async Task Measured_delay_roughly_matches_wall_time()
    {
        long start = QpcClock.GetTimestamp100Ns();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        long elapsedMs = QpcClock.Elapsed100Ns(start, QpcClock.GetTimestamp100Ns()) / 10_000;

        // Wide tolerance: CI runners have coarse timers and noisy scheduling.
        Assert.InRange(elapsedMs, 50, 2_000);
    }
}
