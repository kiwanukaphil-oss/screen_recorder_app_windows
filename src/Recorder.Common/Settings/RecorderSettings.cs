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

    /// <summary>Target bitrate in kbps; null = automatic from resolution/fps/codec.</summary>
    public int? VideoBitrateKbps { get; set; }

    /// <summary>"default" | "cbr" | "vbr" | "cq" — parsed case-insensitively.</summary>
    public string RateControl { get; set; } = "default";

    /// <summary>1–100, used only in constant-quality mode.</summary>
    public int QualityLevel { get; set; } = 70;

    public int KeyframeIntervalSeconds { get; set; } = 2;

    /// <summary>Output resolution as a percentage of capture size (100 = native, 50 = half). GPU-scaled.</summary>
    public int OutputScalePercent { get; set; } = 100;

    /// <summary>Linear gain per source: 100 = unity, 200 = +6 dB, 0 = mute.</summary>
    public int SystemAudioVolumePercent { get; set; } = 100;

    public int MicrophoneVolumePercent { get; set; } = 100;

    public bool CaptureCursor { get; set; } = true;

    public bool VerboseLogging { get; set; }

    /// <summary>Serialized hotkey gesture, e.g. "Ctrl+Shift+F9". Parsed by the hotkey module in M1.</summary>
    public string StartStopHotkey { get; set; } = "Ctrl+Shift+F9";
}
