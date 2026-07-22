namespace Recorder.Common.Timing;

/// <summary>
/// Decides which captured frames to keep so output matches the target frame rate.
/// Capture APIs deliver at display rate (60/120/144 Hz); feeding an encoder faster
/// than its declared frame rate makes Media Foundation queue the excess in memory
/// (root cause of the 2026-07-22 soak crash), so decimation must happen up front.
/// The schedule advances on a fixed cadence to keep accepted frames evenly spaced
/// even when delivery is bursty. Not thread-safe: call from the capture thread only.
/// </summary>
public sealed class FramePacer
{
    private readonly long _frameInterval100Ns;
    private long _nextFrameDue100Ns;

    public FramePacer(int targetFramesPerSecond)
    {
        if (targetFramesPerSecond < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        }
        _frameInterval100Ns = 10_000_000L / targetFramesPerSecond;
    }

    /// <summary>
    /// True if the frame at this timestamp should be kept. On acceptance the next due
    /// time advances by one interval on the fixed cadence; if delivery paused (frame
    /// arrived far beyond the cadence), the schedule re-anchors half an interval before
    /// the frame so an immediately following frame isn't also accepted.
    /// </summary>
    public bool ShouldAccept(long timestamp100Ns)
    {
        if (timestamp100Ns < _nextFrameDue100Ns)
        {
            return false;
        }

        _nextFrameDue100Ns = Math.Max(
            _nextFrameDue100Ns + _frameInterval100Ns,
            timestamp100Ns + _frameInterval100Ns / 2);
        return true;
    }
}
