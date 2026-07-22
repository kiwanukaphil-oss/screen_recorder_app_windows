using System.Diagnostics;

namespace Recorder.Common.Timing;

/// <summary>
/// The single wall-clock reference for the whole pipeline. Every video frame and audio
/// sample is stamped from this clock so audio/video sync is a property of correct
/// timestamps rather than of thread timing (see PLAN.md, principle 3).
/// Values are in 100-nanosecond units, the native unit of Media Foundation sample times.
/// </summary>
public static class QpcClock
{
    private const long TicksPerSecond100Ns = 10_000_000;

    /// <summary>
    /// Current QueryPerformanceCounter reading converted to 100-ns units.
    /// The conversion splits the raw counter into whole seconds and a remainder before
    /// scaling; multiplying the raw counter by 10^7 directly would overflow Int64 after
    /// roughly 100 minutes of machine uptime with a typical 10 MHz QPC frequency.
    /// </summary>
    public static long GetTimestamp100Ns()
    {
        long rawTicks = Stopwatch.GetTimestamp();
        long frequency = Stopwatch.Frequency;

        long wholeSeconds = rawTicks / frequency;
        long remainderTicks = rawTicks % frequency;

        return wholeSeconds * TicksPerSecond100Ns
             + remainderTicks * TicksPerSecond100Ns / frequency;
    }

    /// <summary>Elapsed 100-ns units between two readings of this clock.</summary>
    public static long Elapsed100Ns(long start100Ns, long end100Ns) => end100Ns - start100Ns;
}
