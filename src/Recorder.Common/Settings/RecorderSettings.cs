namespace Recorder.Common.Settings;

/// <summary>Video codec the user asked for; actual encoder selection happens at capture time.</summary>
public enum VideoCodecPreference
{
    H264,
    Hevc,
    Av1,
}

/// <summary>
/// User-facing settings persisted as JSON. M0 carries only the fields the MVP needs;
/// encoder tuning knobs (rate control, presets, keyframe interval) arrive in M2.
/// </summary>
public sealed class RecorderSettings
{
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Recordings");

    public int FramesPerSecond { get; set; } = 60;

    public VideoCodecPreference Codec { get; set; } = VideoCodecPreference.H264;

    public bool CaptureCursor { get; set; } = true;

    public bool VerboseLogging { get; set; }

    /// <summary>Serialized hotkey gesture, e.g. "Ctrl+Shift+F9". Parsed by the hotkey module in M1.</summary>
    public string StartStopHotkey { get; set; } = "Ctrl+Shift+F9";
}
