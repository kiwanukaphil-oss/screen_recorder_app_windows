using Recorder.Capture;
using Recorder.Common.Logging;
using Recorder.Common.Settings;
using Recorder.Common.Timing;
using Recorder.Core;
using Recorder.Graphics;
using Recorder.SyncProbe;
using Serilog.Core;

/// <summary>
/// Player-free A/V sync probe (M2). Records its own fullscreen flash window and
/// simultaneous beeps with an in-process RecordingSession, then prints each event's
/// position on the recording timeline for external flash/beep detection to compare
/// against. Because flash and beep originate from the same QPC read — no media
/// player, no decoder, no third-party sync logic — the measured offset is the
/// recorder pipeline's own video-vs-audio skew (within one display refresh + the
/// 20 ms audio buffer).
///
/// Output: probe-events.csv (event, flash_s, beep_s on the recording timeline) next
/// to the recording, for correlation with ffmpeg blackdetect/silencedetect.
/// </summary>
internal static class Program
{
    private const int EventCount = 10;
    private const int FlashAndBeepMs = 150;
    private const int EventPeriodMs = 1000;

    private static int Main(string[] args)
    {
        string outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "syncprobe");
        Directory.CreateDirectory(outputDirectory);

        using Logger log = RecorderLog.CreateLogger(Path.Combine(outputDirectory, "logs"), verbose: false);

        MonitorInfo monitor = MonitorEnumeration.GetActiveMonitors().First(m => m.IsPrimary);
        var settings = new RecorderSettings { FramesPerSecond = 60 };
        string recordingPath = Path.Combine(outputDirectory, "syncprobe-recording.mp4");

        using var graphicsDevice = new D3D11GraphicsDevice();
        using var session = new RecordingSession(
            graphicsDevice, settings, monitor, recordingPath, log, recordSystemAudio: true);
        using var beepPlayer = new BeepPlayer();
        using var flashWindow = new FlashWindow(
            graphicsDevice.Device, monitor.Left, monitor.Top, monitor.Width, monitor.Height);

        RunProbeSequence(session, flashWindow, beepPlayer, outputDirectory, log);
        return 0;
    }

    /// <summary>
    /// Black lead-in, then per event: request the beep and present the white frame
    /// back-to-back (the Present's post-vsync QPC is the flash time), hold both for
    /// 150 ms, return to black for the rest of the second. Event times are converted
    /// to the recording timeline using the session's published base timestamp and
    /// written as CSV for the analysis step.
    /// </summary>
    private static void RunProbeSequence(
        RecordingSession session, FlashWindow flashWindow, BeepPlayer beepPlayer,
        string outputDirectory, Logger log)
    {
        flashWindow.Present(white: false);
        session.Start();
        long base100Ns = session.BaseTimestamp100Ns;

        Thread.Sleep(2000);
        flashWindow.PumpMessages();

        var events = new List<(int Index, double FlashSeconds, double BeepSeconds)>();
        for (int i = 0; i < EventCount; i++)
        {
            long beepQpc = beepPlayer.FireBeep(FlashAndBeepMs);
            flashWindow.Present(white: true);
            long flashQpc = QpcClock.GetTimestamp100Ns();

            events.Add((i, (flashQpc - base100Ns) / 1e7, (beepQpc - base100Ns) / 1e7));

            Thread.Sleep(FlashAndBeepMs);
            flashWindow.Present(white: false);
            flashWindow.PumpMessages();
            Thread.Sleep(EventPeriodMs - FlashAndBeepMs);
        }

        Thread.Sleep(1000);
        session.Stop();

        string eventsPath = Path.Combine(outputDirectory, "probe-events.csv");
        File.WriteAllLines(eventsPath, new[] { "event,flash_s,beep_s" }
            .Concat(events.Select(e => $"{e.Index},{e.FlashSeconds:F4},{e.BeepSeconds:F4}")));

        log.Information("Probe complete: {Events} events, recording {File}, events {Csv}",
            events.Count, session.OutputFilePath, eventsPath);
    }
}
