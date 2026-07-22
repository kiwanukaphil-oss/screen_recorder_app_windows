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
    private readonly IReadOnlyList<MonitorInfo> _monitors;

    private D3D11GraphicsDevice? _graphicsDevice;
    private RecordingSession? _session;
    private GlobalStopHotkey? _stopHotkey;
    private DispatcherTimer? _statusTimer;
    private DateTime _recordingStartedUtc;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Screen Recorder";
        AppWindow.Resize(new SizeInt32(460, 640));

        string settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Recorder");
        _log = RecorderLog.CreateLogger(Path.Combine(settingsDirectory, "logs"), verbose: false);
        _settingsStore = new JsonSettingsStore(Path.Combine(settingsDirectory, "settings.json"));
        _settings = _settingsStore.Load(out _);

        _monitors = MonitorEnumeration.GetActiveMonitors();
        foreach (MonitorInfo monitor in _monitors)
        {
            MonitorPicker.Items.Add(
                $"{monitor.DeviceName.TrimStart('\\', '.')} ({monitor.Width}x{monitor.Height}){(monitor.IsPrimary ? " — primary" : "")}");
        }
        MonitorPicker.SelectedIndex = Math.Max(0, _monitors.ToList().FindIndex(m => m.IsPrimary));

        Closed += (_, _) => StopRecordingIfActive();
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

        MonitorInfo monitor = _monitors[Math.Max(0, MonitorPicker.SelectedIndex)];
        bool systemAudio = SystemAudioToggle.IsOn;
        bool microphone = MicrophoneToggle.IsOn;

        Directory.CreateDirectory(_settings.OutputDirectory);
        string outputFile = Path.Combine(
            _settings.OutputDirectory, $"recording-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

        await Task.Run(() =>
        {
            _graphicsDevice = new D3D11GraphicsDevice();
            _session = new RecordingSession(
                _graphicsDevice, _settings, monitor, outputFile, _log, systemAudio, microphone);
            _session.Start();
        });

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

        string file = session.OutputFilePath;
        double sizeMb = new FileInfo(file).Length / 1_048_576.0;
        StatusText.Text = $"Saved {Path.GetFileName(file)} ({sizeMb:0.0} MB) — " +
                          $"{session.Statistics.FramesWritten} frames, {session.Statistics.FramesDropped} dropped.";

        CleanUpSession();
        RecordButtonLabel.Text = "● Start recording";
    }

    private void RefreshRecordingStatus()
    {
        if (_session is null)
        {
            return;
        }
        if (_session.HasFailed)
        {
            StatusText.Text = "Recording failed — stopping…";
            _ = StopRecordingAsync();
            return;
        }

        TimeSpan elapsed = DateTime.UtcNow - _recordingStartedUtc;
        StatusText.Text = $"Recording {elapsed:hh\\:mm\\:ss} — " +
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
