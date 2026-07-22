using Microsoft.UI.Xaml;
using Recorder.Capture;
using Recorder.Common.Logging;
using Recorder.Common.Settings;
using Recorder.Core;
using Recorder.Graphics;
using Serilog.Core;
using Windows.Graphics;

namespace Recorder.App;

/// <summary>
/// M4 slice 1: a functional single-window shell over RecordingSession. Deliberately
/// code-behind for now — the MVVM split, tray icon, settings page and profiles come
/// with the rest of M4. All recording work runs off the UI thread; the UI only polls
/// statistics on a dispatcher timer.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Logger _log;
    private readonly JsonSettingsStore _settingsStore;
    private readonly RecorderSettings _settings;

    /// <summary>Backing objects for SourcePicker items: MonitorInfo or CapturableWindow, same order.</summary>
    private readonly List<object> _sources = new();

    private D3D11GraphicsDevice? _graphicsDevice;
    private RecordingSession? _session;
    private GlobalStopHotkey? _stopHotkey;
    private DispatcherTimer? _statusTimer;
    private DateTime _recordingStartedUtc;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Screen Recorder";
        AppWindow.Resize(new SizeInt32(520, 700));

        string settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Recorder");
        _log = RecorderLog.CreateLogger(Path.Combine(settingsDirectory, "logs"), verbose: false);
        _settingsStore = new JsonSettingsStore(Path.Combine(settingsDirectory, "settings.json"));
        _settings = _settingsStore.Load(out _);

        PopulateSourcePicker();
        Closed += (_, _) => StopRecordingIfActive();
    }

    /// <summary>
    /// Fills the source list: monitors first (primary preselected), then every
    /// capturable window. Re-run each time the dropdown opens so newly opened apps
    /// appear without restarting.
    /// </summary>
    private void PopulateSourcePicker()
    {
        object? previousSelection = SourcePicker.SelectedIndex >= 0 && SourcePicker.SelectedIndex < _sources.Count
            ? _sources[SourcePicker.SelectedIndex]
            : null;

        _sources.Clear();
        SourcePicker.Items.Clear();

        foreach (MonitorInfo monitor in MonitorEnumeration.GetActiveMonitors())
        {
            _sources.Add(monitor);
            SourcePicker.Items.Add(
                $"🖥 {monitor.DeviceName.TrimStart('\\', '.')} ({monitor.Width}x{monitor.Height}){(monitor.IsPrimary ? " — primary" : "")}");
        }
        foreach (CapturableWindow window in WindowEnumeration.GetCapturableWindows())
        {
            _sources.Add(window);
            string title = window.Title.Length > 60 ? window.Title[..57] + "…" : window.Title;
            SourcePicker.Items.Add($"🗔 {title}");
        }

        int restoredIndex = previousSelection is MonitorInfo prevMonitor
            ? _sources.FindIndex(s => s is MonitorInfo m && m.Handle == prevMonitor.Handle)
            : previousSelection is CapturableWindow prevWindow
                ? _sources.FindIndex(s => s is CapturableWindow w && w.Handle == prevWindow.Handle)
                : -1;
        SourcePicker.SelectedIndex = restoredIndex >= 0
            ? restoredIndex
            : Math.Max(0, _sources.FindIndex(s => s is MonitorInfo m && m.IsPrimary));
    }

    private void OnSourcePickerOpened(object? sender, object args) => PopulateSourcePicker();

    private void OnPauseButtonClick(object sender, RoutedEventArgs args)
    {
        if (_session is null)
        {
            return;
        }
        if (_session.IsPaused)
        {
            _session.Resume();
            PauseButtonLabel.Text = "⏸ Pause";
        }
        else
        {
            _session.Pause();
            PauseButtonLabel.Text = "▶ Resume";
        }
    }

    private bool IsRecording => _session is not null;

    /// <summary>
    /// Toggles recording. Start builds the whole pipeline on a background thread
    /// (device + Media Foundation init takes ~300 ms — never block the UI), then
    /// arms the global stop hotkey and a 500 ms status timer.
    /// </summary>
    private async void OnRecordButtonClick(object sender, RoutedEventArgs args)
    {
        RecordButton.IsEnabled = false;
        try
        {
            if (!IsRecording)
            {
                await StartRecordingAsync();
            }
            else
            {
                await StopRecordingAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Recording operation failed");
            StatusText.Text = $"Error: {ex.Message}";
            CleanUpSession();
            RecordButtonLabel.Text = "● Start recording";
        }
        finally
        {
            RecordButton.IsEnabled = true;
        }
    }

    private async Task StartRecordingAsync()
    {
        _settings.FramesPerSecond = FpsPicker.SelectedIndex == 0 ? 30 : 60;
        _settings.Codec = CodecPicker.SelectedIndex == 1 ? VideoCodecPreference.Hevc : VideoCodecPreference.H264;
        _settingsStore.Save(_settings);

        object source = _sources[Math.Max(0, SourcePicker.SelectedIndex)];
        bool systemAudio = SystemAudioToggle.IsOn;
        bool microphone = MicrophoneToggle.IsOn;

        Directory.CreateDirectory(_settings.OutputDirectory);
        string outputFile = Path.Combine(
            _settings.OutputDirectory, $"recording-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

        await Task.Run(() =>
        {
            _graphicsDevice = new D3D11GraphicsDevice();
            _session = source switch
            {
                MonitorInfo monitor => new RecordingSession(
                    _graphicsDevice, _settings, monitor, outputFile, _log, systemAudio, microphone),
                CapturableWindow window => new RecordingSession(
                    _graphicsDevice, _settings, window, outputFile, _log, systemAudio, microphone),
                _ => throw new InvalidOperationException("Unknown capture source type."),
            };
            _session.Start();
        });
        PauseButton.IsEnabled = true;

        _stopHotkey = new GlobalStopHotkey(() => DispatcherQueue.TryEnqueue(async () =>
        {
            if (IsRecording)
            {
                await StopRecordingAsync();
            }
        }));

        _recordingStartedUtc = DateTime.UtcNow;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RefreshRecordingStatus();
        _statusTimer.Start();

        RecordButtonLabel.Text = "■ Stop recording";
    }

    private async Task StopRecordingAsync()
    {
        RecordingSession? session = _session;
        if (session is null)
        {
            return;
        }

        _statusTimer?.Stop();
        await Task.Run(session.Stop);

        string file = session.OutputFiles[^1];
        double sizeMb = new FileInfo(file).Length / 1_048_576.0;
        string parts = session.OutputFiles.Count > 1 ? $" in {session.OutputFiles.Count} parts" : "";
        string autoStop = session.AutoStopReason is string reason ? $" (auto-stopped: {reason})" : "";
        StatusText.Text = $"Saved {Path.GetFileName(file)} ({sizeMb:0.0} MB){parts} — " +
                          $"{session.Statistics.FramesWritten} frames, {session.Statistics.FramesDropped} dropped.{autoStop}";

        CleanUpSession();
        RecordButtonLabel.Text = "● Start recording";
        PauseButton.IsEnabled = false;
        PauseButtonLabel.Text = "⏸ Pause";
    }

    private void RefreshRecordingStatus()
    {
        if (_session is null)
        {
            return;
        }
        if (_session.HasFailed || _session.AutoStopReason is not null)
        {
            StatusText.Text = _session.AutoStopReason ?? "Recording failed — stopping…";
            _ = StopRecordingAsync();
            return;
        }

        TimeSpan elapsed = DateTime.UtcNow - _recordingStartedUtc;
        string state = _session.IsPaused ? "Paused at" : "Recording";
        StatusText.Text = $"{state} {elapsed:hh\\:mm\\:ss} — " +
                          $"{_session.Statistics.FramesWritten} frames written, " +
                          $"{_session.Statistics.FramesDropped} dropped.";
    }

    private void StopRecordingIfActive()
    {
        if (_session is not null)
        {
            try
            {
                _session.Stop();
            }
            catch
            {
                // Window is closing; the session's own error handling already logged.
            }
            CleanUpSession();
        }
    }

    private void CleanUpSession()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
        _stopHotkey?.Dispose();
        _stopHotkey = null;
        _session?.Dispose();
        _session = null;
        _graphicsDevice?.Dispose();
        _graphicsDevice = null;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs args)
    {
        Directory.CreateDirectory(_settings.OutputDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _settings.OutputDirectory,
            UseShellExecute = true,
        });
    }
}
