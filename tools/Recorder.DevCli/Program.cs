using Recorder.Capture;
using Recorder.Common.Logging;
using Recorder.Common.Settings;
using Recorder.Core;
using Recorder.Graphics;
using Serilog.Core;

/// <summary>
/// Developer CLI for the M1 recorder engine (the WinUI shell arrives in M4).
/// Usage: Recorder.DevCli [--seconds N] [--monitor INDEX] [--no-audio] [--fps N] [--output DIR]
/// Without --seconds, recording runs until Enter or the global Ctrl+Shift+F9 hotkey.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int? seconds = ReadIntOption(args, "--seconds");
        int monitorIndex = ReadIntOption(args, "--monitor") ?? 0;
        bool recordAudio = !args.Contains("--no-audio");
        bool recordMicrophone = args.Contains("--mic");

        var settings = new RecorderSettings();
        if (ReadIntOption(args, "--fps") is int fps)
        {
            settings.FramesPerSecond = fps;
        }
        if (ReadStringOption(args, "--codec") is string codec)
        {
            settings.Codec = codec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
                ? VideoCodecPreference.Hevc
                : VideoCodecPreference.H264;
        }
        if (ReadStringOption(args, "--rate") is string rate)
        {
            settings.RateControl = rate;
        }
        if (ReadIntOption(args, "--bitrate") is int bitrateKbps)
        {
            settings.VideoBitrateKbps = bitrateKbps;
        }
        if (ReadIntOption(args, "--quality") is int quality)
        {
            settings.QualityLevel = quality;
        }
        string outputDirectory = ReadStringOption(args, "--output") ?? settings.OutputDirectory;
        Directory.CreateDirectory(outputDirectory);

        using Logger log = RecorderLog.CreateLogger(Path.Combine(outputDirectory, "logs"), settings.VerboseLogging);

        IReadOnlyList<MonitorInfo> monitors = MonitorEnumeration.GetActiveMonitors();
        if (monitorIndex >= monitors.Count)
        {
            log.Error("Monitor index {Index} not found ({Count} monitors present)", monitorIndex, monitors.Count);
            return 2;
        }

        string outputFile = Path.Combine(
            outputDirectory,
            $"recording-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

        try
        {
            RunRecording(settings, monitors[monitorIndex], outputFile, recordAudio, recordMicrophone, seconds, log);
            return 0;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Recording failed");
            return 1;
        }
    }

    /// <summary>
    /// Runs one complete recording: build the pipeline, start it, wait for whichever
    /// stop signal comes first (timer, Enter, or global hotkey), then stop and print
    /// the session statistics that the M1 exit-gate checks (drops must be 0 on a
    /// desktop workload).
    /// </summary>
    private static void RunRecording(
        RecorderSettings settings,
        MonitorInfo monitor,
        string outputFile,
        bool recordAudio,
        bool recordMicrophone,
        int? seconds,
        Logger log)
    {
        using var graphicsDevice = new D3D11GraphicsDevice();
        log.Information("GPU: {Adapter}", graphicsDevice.GetAdapterDescription());

        using var session = new RecordingSession(
            graphicsDevice, settings, monitor, outputFile, log, recordAudio, recordMicrophone);
        using var stopSignal = new ManualResetEventSlim();
        using var hotkey = new GlobalStopHotkey(stopSignal.Set);
        log.Information(hotkey.RegistrationSucceeded
            ? "Stop with Ctrl+Shift+F9 (global) or Enter"
            : "Global hotkey unavailable; stop with Enter");

        session.Start();
        log.Information("Recording started");

        if (seconds is not int)
        {
            var enterThread = new Thread(() => { Console.ReadLine(); stopSignal.Set(); })
            {
                IsBackground = true,
            };
            enterThread.Start();
        }

        // Waits in short slices so a mux-thread failure (e.g. the memory fail-safe
        // tripping) ends the recording promptly instead of after the full duration.
        DateTime deadline = seconds is int limit
            ? DateTime.UtcNow.AddSeconds(limit)
            : DateTime.MaxValue;
        while (!stopSignal.Wait(TimeSpan.FromMilliseconds(500)))
        {
            if (session.HasFailed || DateTime.UtcNow >= deadline)
            {
                break;
            }
        }

        session.Stop();

        var fileInfo = new FileInfo(outputFile);
        log.Information("Wrote {File} ({Size:0.0} MB)", fileInfo.FullName, fileInfo.Length / 1_048_576.0);
        log.Information("Frames: {Written} written / {Dropped} dropped; audio chunks: {Audio}; silence gaps: {Gaps}",
            session.Statistics.FramesWritten, session.Statistics.FramesDropped,
            session.Statistics.AudioChunksWritten, session.Statistics.SilenceGapsFilled);
    }

    private static int? ReadIntOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value)
            ? value
            : null;
    }

    private static string? ReadStringOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
