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

    /// <summary>
    /// Write fragmented MP4 so a crash/power loss leaves a playable file. Limitation:
    /// supports one audio track — with mic AND system audio enabled the session falls
    /// back to a standard MP4 and logs a warning.
    /// </summary>
    public bool CrashSafeContainer { get; set; } = true;

    /// <summary>Start a new file after this many seconds of recording (null = never).</summary>
    public int? SplitMaxDurationSeconds { get; set; }

    /// <summary>Start a new file when the current one exceeds this size (null = never).</summary>
    public int? SplitMaxSizeMb { get; set; }

    /// <summary>Stop recording gracefully when free disk space falls below this.</summary>
    public int MinFreeDiskGb { get; set; } = 2;

    /// <summary>"mp4" (native) | "mkv" | "mov" — non-MP4 containers are remuxed after recording (needs ffmpeg).</summary>
    public string OutputContainer { get; set; } = "mp4";

    public bool CaptureCursor { get; set; } = true;

    /// <summary>User-facing capture preferences kept alongside encoder settings.</summary>
    public bool RecordSystemAudio { get; set; } = true;

    public bool RecordMicrophone { get; set; }

    /// <summary>Optional preparation countdown. Supported UI values are 0, 3 and 5 seconds.</summary>
    public int CountdownSeconds { get; set; } = 3;

    /// <summary>Last simple recording profile selected in the app.</summary>
    public string QualityPreset { get; set; } = "Balanced";

    public bool VerboseLogging { get; set; }

    /// <summary>Serialized hotkey gesture, e.g. "Ctrl+Shift+F9". Parsed by the hotkey module in M1.</summary>
    public string StartStopHotkey { get; set; } = "Ctrl+Shift+F9";

    public string PauseResumeHotkey { get; set; } = "Ctrl+Shift+F10";
}
