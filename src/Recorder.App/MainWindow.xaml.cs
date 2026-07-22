using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Recorder.Capture;
using Recorder.Common.Logging;
using Recorder.Common.Settings;
using Recorder.Core;
using Recorder.Graphics;
using Serilog.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Recorder.App;

/// <summary>
/// Premium single-window recording control center. Recording operations are modeled
/// explicitly so every UI, hotkey and watchdog path shares one safe start/stop flow.
/// </summary>
public sealed partial class MainWindow : Window
{
    private enum RecordingUiState
    {
        Idle,
        Countdown,
        Starting,
        Recording,
        Paused,
        Stopping,
        Error,
    }

    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    private readonly Logger _log;
    private readonly JsonSettingsStore _settingsStore;
    private readonly RecorderSettings _settings;
    private readonly List<object> _sources = new();

    private D3D11GraphicsDevice? _graphicsDevice;
    private RecordingSession? _session;
    private GlobalHotkey? _startStopHotkey;
    private GlobalHotkey? _pauseResumeHotkey;
    private TrayIconService? _trayIcon;
    private RecordingOverlayWindow? _recordingOverlay;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _settingsSaveTimer;
    private CancellationTokenSource? _countdownCancellation;
    private Task? _startTask;
    private Task? _stopTask;

    private RecordingUiState _uiState = RecordingUiState.Idle;
    private DateTime _activeSegmentStartedUtc;
    private TimeSpan _recordedElapsed;
    private string? _latestOutputFile;
    // InitializeComponent can raise Slider/ComboBox events before the constructor has
    // loaded settings; keep handlers inert until InitializeControlsFromSettings ends.
    private bool _initializingControls = true;
    private bool _captureExclusionAttempted;
    private bool _captureExclusionApplied;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _stopAfterStartRequested;
    private bool _mainWindowHiddenToTray;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Screen Recorder";
        AppWindow.Resize(new SizeInt32(760, 880));

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica is an enhancement; the theme background remains fully usable.
        }

        string settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Recorder");
        _settingsStore = new JsonSettingsStore(Path.Combine(settingsDirectory, "settings.json"));
        _settings = _settingsStore.Load(out bool loadedFromDisk);
        _log = RecorderLog.CreateLogger(
            Path.Combine(settingsDirectory, "logs"), _settings.VerboseLogging);

        InitializeTrayAndOverlay();
        InitializeControlsFromSettings();
        PopulateSourcePicker();
        RefreshStorageSummary();
        SetUiState(RecordingUiState.Idle);

        if (!loadedFromDisk)
        {
            ShowMessage(
                "Ready to record",
                "Your recordings will use balanced, high-quality defaults. You can fine-tune them under Advanced.",
                InfoBarSeverity.Informational);
        }

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
        RegisterGlobalHotkeys(showFeedback: true);
    }

    private void InitializeTrayAndOverlay()
    {
        try
        {
            _trayIcon = new TrayIconService();
            _trayIcon.OpenRequested += () => DispatcherQueue.TryEnqueue(ShowMainWindow);
            _trayIcon.StartStopRequested += () =>
                DispatcherQueue.TryEnqueue(() => _ = HandleGlobalStartStopAsync());
            _trayIcon.PauseResumeRequested += () => DispatcherQueue.TryEnqueue(HandleGlobalPauseResume);
            _trayIcon.ToggleOverlayRequested += () => DispatcherQueue.TryEnqueue(ToggleRecordingOverlay);
            _trayIcon.ExitRequested += () => DispatcherQueue.TryEnqueue(() => _ = ExitApplicationAsync());

        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Notification-area initialization failed");
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

    private void CreateFreshRecordingOverlay()
    {
        DisposeRecordingOverlay();

        _recordingOverlay = new RecordingOverlayWindow();
        _recordingOverlay.ConfigureActions(
            () => DispatcherQueue.TryEnqueue(HandleGlobalPauseResume),
            () => DispatcherQueue.TryEnqueue(() => _ = StopRecordingSafelyAsync()),
            () => DispatcherQueue.TryEnqueue(HideRecordingOverlay));
    }

    private void DisposeRecordingOverlay()
    {
        _recordingOverlay?.CloseController();
        _recordingOverlay = null;
    }

    private void InitializeControlsFromSettings()
    {
        _initializingControls = true;
        try
        {
            SystemAudioToggle.IsOn = _settings.RecordSystemAudio;
            MicrophoneToggle.IsOn = _settings.RecordMicrophone;
            SystemVolumeSlider.Value = Math.Clamp(_settings.SystemAudioVolumePercent, 0, 200);
            MicrophoneVolumeSlider.Value = Math.Clamp(_settings.MicrophoneVolumePercent, 0, 200);
            CaptureCursorToggle.IsOn = _settings.CaptureCursor;
            CrashSafeToggle.IsOn = _settings.CrashSafeContainer;
            QualityLevelSlider.Value = Math.Clamp(_settings.QualityLevel, 1, 100);

            CodecPicker.SelectedIndex = _settings.Codec == VideoCodecPreference.Hevc ? 1 : 0;
            SelectByTag(FpsPicker, Math.Clamp(_settings.FramesPerSecond, 30, 120).ToString());
            SelectByTag(ScalePicker, ClosestScale(_settings.OutputScalePercent).ToString());
            SelectByTag(CountdownPicker, ClosestCountdown(_settings.CountdownSeconds).ToString());
            SelectByTag(RateControlPicker, _settings.RateControl, fallbackIndex: 0);
            SelectByContent(HotkeyPicker, _settings.StartStopHotkey, fallbackIndex: 0);
            SelectByContent(PauseHotkeyPicker, _settings.PauseResumeHotkey, fallbackIndex: 0);
            SelectByTag(QualityPresetPicker, _settings.QualityPreset, fallbackIndex: 3);

            OutputDirectoryText.Text = _settings.OutputDirectory;
            UpdateAudioControlAvailability();
            UpdateAdvancedControlAvailability();
        }
        finally
        {
            _initializingControls = false;
        }

        RefreshQualitySummary();
        RefreshReadySummary();
    }

    private static int ClosestScale(int value) => value switch
    {
        >= 88 => 100,
        >= 63 => 75,
        _ => 50,
    };

    private static int ClosestCountdown(int value) => value switch
    {
        <= 1 => 0,
        <= 4 => 3,
        _ => 5,
    };

    private static void SelectByTag(ComboBox picker, string value, int fallbackIndex = 0)
    {
        for (int index = 0; index < picker.Items.Count; index++)
        {
            if (picker.Items[index] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                picker.SelectedIndex = index;
                return;
            }
        }
        picker.SelectedIndex = fallbackIndex;
    }

    private static void SelectByContent(ComboBox picker, string value, int fallbackIndex = 0)
    {
        for (int index = 0; index < picker.Items.Count; index++)
        {
            if (picker.Items[index] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                picker.SelectedIndex = index;
                return;
            }
        }
        picker.SelectedIndex = fallbackIndex;
    }

    private void PopulateSourcePicker()
    {
        object? previousSelection = SourcePicker.SelectedIndex >= 0 && SourcePicker.SelectedIndex < _sources.Count
            ? _sources[SourcePicker.SelectedIndex]
            : null;

        IntPtr ownWindow = WindowNative.GetWindowHandle(this);
        _sources.Clear();
        SourcePicker.Items.Clear();

        foreach (MonitorInfo monitor in MonitorEnumeration.GetActiveMonitors())
        {
            _sources.Add(monitor);
            string primary = monitor.IsPrimary ? " · Primary" : "";
            SourcePicker.Items.Add(
                $"Screen · {monitor.DeviceName.TrimStart('\\', '.')} · {monitor.Width} × {monitor.Height}{primary}");
        }

        foreach (CapturableWindow window in WindowEnumeration.GetCapturableWindows())
        {
            GetWindowThreadProcessId(window.Handle, out uint processId);
            if (window.Handle == ownWindow || processId == Environment.ProcessId)
            {
                continue;
            }

            _sources.Add(window);
            string title = window.Title.Length > 72 ? window.Title[..69] + "…" : window.Title;
            SourcePicker.Items.Add($"Window · {title}");
        }

        int restoredIndex = previousSelection is MonitorInfo previousMonitor
            ? _sources.FindIndex(source => source is MonitorInfo monitor && monitor.Handle == previousMonitor.Handle)
            : previousSelection is CapturableWindow previousWindow
                ? _sources.FindIndex(source => source is CapturableWindow window && window.Handle == previousWindow.Handle)
                : -1;

        SourcePicker.SelectedIndex = restoredIndex >= 0
            ? restoredIndex
            : _sources.FindIndex(source => source is MonitorInfo monitor && monitor.IsPrimary);

        if (SourcePicker.SelectedIndex < 0 && _sources.Count > 0)
        {
            SourcePicker.SelectedIndex = 0;
        }

        RefreshSourceSummary();
    }

    private void OnSourcePickerOpened(object? sender, object args)
    {
        if (_uiState is RecordingUiState.Idle or RecordingUiState.Error)
        {
            PopulateSourcePicker();
        }
    }

    private void OnRefreshSourcesClick(object sender, RoutedEventArgs args) => PopulateSourcePicker();

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        RefreshSourceSummary();
        RefreshQualitySummary();
        RefreshStorageSummary();
    }

    private void RefreshSourceSummary()
    {
        object? source = GetSelectedSource();
        switch (source)
        {
            case MonitorInfo monitor:
                PreviewSourceName.Text = monitor.IsPrimary ? "Primary display" : monitor.DeviceName.TrimStart('\\', '.');
                PreviewSourceDetails.Text = $"{monitor.Width} × {monitor.Height} · Entire screen";
                break;
            case CapturableWindow window:
                PreviewSourceName.Text = window.Title;
                PreviewSourceDetails.Text = "Application window · Isolated capture";
                break;
            default:
                PreviewSourceName.Text = "No capture source available";
                PreviewSourceDetails.Text = "Refresh after connecting a display or opening an app";
                break;
        }
    }

    private object? GetSelectedSource() =>
        SourcePicker.SelectedIndex >= 0 && SourcePicker.SelectedIndex < _sources.Count
            ? _sources[SourcePicker.SelectedIndex]
            : null;

    private void OnQualityPresetChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_initializingControls || QualityPresetPicker.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string preset = item.Tag?.ToString() ?? "Custom";
        _initializingControls = true;
        try
        {
            switch (preset)
            {
                case "Balanced":
                    CodecPicker.SelectedIndex = 0;
                    SelectByTag(FpsPicker, "60");
                    SelectByTag(ScalePicker, "100");
                    break;
                case "Efficient":
                    CodecPicker.SelectedIndex = 1;
                    SelectByTag(FpsPicker, "60");
                    SelectByTag(ScalePicker, "100");
                    break;
                case "Smoothest":
                    CodecPicker.SelectedIndex = 0;
                    SelectByTag(FpsPicker, "120");
                    SelectByTag(ScalePicker, "100");
                    break;
            }
        }
        finally
        {
            _initializingControls = false;
        }

        ScheduleSettingsCommit();
    }

    private void OnAdvancedSettingChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_initializingControls)
        {
            return;
        }

        _initializingControls = true;
        QualityPresetPicker.SelectedIndex = 3;
        _initializingControls = false;
        UpdateAdvancedControlAvailability();
        ScheduleSettingsCommit();
    }

    private void OnQualityLevelChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (!_initializingControls)
        {
            ScheduleSettingsCommit();
        }
    }

    private void OnAudioToggleChanged(object sender, RoutedEventArgs args)
    {
        if (_initializingControls)
        {
            return;
        }
        UpdateAudioControlAvailability();
        ScheduleSettingsCommit();
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (!_initializingControls)
        {
            ScheduleSettingsCommit();
        }
    }

    private void OnPreferenceChanged(object sender, RoutedEventArgs args)
    {
        if (!_initializingControls)
        {
            ScheduleSettingsCommit();
        }
    }

    private void OnPreferenceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_initializingControls)
        {
            ScheduleSettingsCommit();
            if (ReferenceEquals(sender, HotkeyPicker) || ReferenceEquals(sender, PauseHotkeyPicker))
            {
                RegisterGlobalHotkeys(showFeedback: true);
            }
        }
    }

    private void UpdateAudioControlAvailability()
    {
        SystemVolumeSlider.IsEnabled = SystemAudioToggle.IsOn;
        MicrophoneVolumeSlider.IsEnabled = MicrophoneToggle.IsOn;
    }

    private void UpdateAdvancedControlAvailability()
    {
        QualityLevelSlider.IsEnabled =
            (RateControlPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "cq";
    }

    private void ScheduleSettingsCommit()
    {
        CommitSettingsFromControls(persist: false);

        if (_settingsSaveTimer is null)
        {
            _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _settingsSaveTimer.Tick += (_, _) =>
            {
                _settingsSaveTimer?.Stop();
                SaveSettings();
            };
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void CommitSettingsFromControls(bool persist = true)
    {
        _settings.Codec = CodecPicker.SelectedIndex == 1
            ? VideoCodecPreference.Hevc
            : VideoCodecPreference.H264;
        _settings.FramesPerSecond = SelectedIntTag(FpsPicker, 60);
        _settings.OutputScalePercent = SelectedIntTag(ScalePicker, 100);
        _settings.CountdownSeconds = SelectedIntTag(CountdownPicker, 3);
        _settings.RecordSystemAudio = SystemAudioToggle.IsOn;
        _settings.RecordMicrophone = MicrophoneToggle.IsOn;
        _settings.SystemAudioVolumePercent = (int)Math.Round(SystemVolumeSlider.Value);
        _settings.MicrophoneVolumePercent = (int)Math.Round(MicrophoneVolumeSlider.Value);
        _settings.CaptureCursor = CaptureCursorToggle.IsOn;
        _settings.CrashSafeContainer = CrashSafeToggle.IsOn;
        _settings.RateControl = (RateControlPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default";
        _settings.QualityLevel = (int)Math.Round(QualityLevelSlider.Value);
        _settings.QualityPreset = (QualityPresetPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Custom";
        _settings.StartStopHotkey = (HotkeyPicker.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? "Ctrl+Shift+F9";
        _settings.PauseResumeHotkey = (PauseHotkeyPicker.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? "Ctrl+Shift+F10";

        if (persist)
        {
            _settingsSaveTimer?.Stop();
            SaveSettings();
        }

        RefreshQualitySummary();
        RefreshStorageSummary();
        RefreshReadySummary();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Could not persist recorder settings");
        }
    }

    private static int SelectedIntTag(ComboBox picker, int fallback) =>
        picker.SelectedItem is ComboBoxItem item &&
        int.TryParse(item.Tag?.ToString(), out int value)
            ? value
            : fallback;

    private void RefreshQualitySummary()
    {
        (int sourceWidth, int sourceHeight) = GetSourceDimensions();
        int scale = SelectedIntTag(ScalePicker, 100);
        int width = Math.Max(2, sourceWidth * scale / 100) & ~1;
        int height = Math.Max(2, sourceHeight * scale / 100) & ~1;
        int fps = SelectedIntTag(FpsPicker, 60);
        string codec = CodecPicker.SelectedIndex == 1 ? "HEVC" : "H.264";
        double bitrateMbps = EstimateBitrate(width, height, fps, CodecPicker.SelectedIndex == 1) / 1_000_000d;

        string resolution = sourceWidth > 0 ? $"{width} × {height}" : "Native resolution";
        QualitySummaryText.Text = $"{resolution} · {fps} FPS · {codec} · ~{bitrateMbps:0.#} Mbps";
    }

    private (int Width, int Height) GetSourceDimensions() => GetSelectedSource() switch
    {
        MonitorInfo monitor => (monitor.Width, monitor.Height),
        _ => (0, 0),
    };

    private static int EstimateBitrate(int width, int height, int fps, bool hevc)
    {
        if (width <= 0 || height <= 0)
        {
            width = 1920;
            height = 1080;
        }

        double bitsPerPixel = hevc ? 0.06 : 0.09;
        long bits = (long)(width * (long)height * fps * bitsPerPixel);
        return (int)Math.Clamp(bits, 2_000_000, 120_000_000);
    }

    private async void OnChooseFolderClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            _settings.OutputDirectory = folder.Path;
            OutputDirectoryText.Text = folder.Path;
            _settingsStore.Save(_settings);
            RefreshStorageSummary();
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Output folder selection failed");
            ShowMessage(
                "Folder unavailable",
                "That folder could not be selected. Choose a local folder where you have write access.",
                InfoBarSeverity.Warning);
        }
    }

    private void RefreshStorageSummary()
    {
        OutputDirectoryText.Text = _settings.OutputDirectory;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(_settings.OutputDirectory));
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException("No drive is available for the output path.");
            }

            var drive = new DriveInfo(root);
            double freeGb = drive.AvailableFreeSpace / 1_073_741_824d;
            (int sourceWidth, int sourceHeight) = GetSourceDimensions();
            int scale = SelectedIntTag(ScalePicker, 100);
            int bitrate = EstimateBitrate(
                Math.Max(2, sourceWidth * scale / 100),
                Math.Max(2, sourceHeight * scale / 100),
                SelectedIntTag(FpsPicker, 60),
                CodecPicker.SelectedIndex == 1);
            double usableBytes = Math.Max(0, drive.AvailableFreeSpace - _settings.MinFreeDiskGb * 1_073_741_824d);
            TimeSpan estimate = TimeSpan.FromSeconds(usableBytes * 8 / bitrate);

            string estimateText = estimate.TotalHours >= 1
                ? $"about {Math.Floor(estimate.TotalHours):0}h {estimate.Minutes}m"
                : $"about {Math.Max(0, estimate.Minutes)} minutes";
            StorageSummaryText.Text = $"{freeGb:0.#} GB free · {estimateText} available at current quality";
        }
        catch
        {
            StorageSummaryText.Text = "Storage availability will be checked before recording";
        }
    }

    private void RefreshReadySummary()
    {
        string countdown = SelectedIntTag(CountdownPicker, 3) switch
        {
            0 => "Starts immediately",
            int seconds => $"{seconds}-second countdown",
        };
        ReadySummaryText.Text =
            $"{countdown} · {_settings.StartStopHotkey} starts/stops · " +
            $"{_settings.PauseResumeHotkey} pauses/resumes";
    }

    private async void OnStartButtonClick(object sender, RoutedEventArgs args)
    {
        if (_uiState == RecordingUiState.Countdown)
        {
            _countdownCancellation?.Cancel();
            return;
        }

        await StartRecordingSafelyAsync(triggeredByHotkey: false);
    }

    private async Task StartRecordingSafelyAsync(bool triggeredByHotkey)
    {
        if (_uiState is not (RecordingUiState.Idle or RecordingUiState.Error) || _startTask is not null)
        {
            return;
        }

        _stopAfterStartRequested = false;
        _startTask = BeginRecordingAsync(triggeredByHotkey);
        try
        {
            await _startTask;
        }
        finally
        {
            _startTask = null;
        }
    }

    private async Task BeginRecordingAsync(bool triggeredByHotkey)
    {
        ResultPanel.Visibility = Visibility.Collapsed;
        MessageBar.IsOpen = false;
        CommitSettingsFromControls();

        string? preflightError = ValidatePreflight();
        if (preflightError is not null)
        {
            SetUiState(RecordingUiState.Error);
            ShowMessage("Not ready to record", preflightError, InfoBarSeverity.Error);
            return;
        }

        if (!triggeredByHotkey && !await ConfirmAudioCompatibilityAsync())
        {
            SetUiState(RecordingUiState.Idle);
            return;
        }
        if (triggeredByHotkey && _settings.CrashSafeContainer &&
            _settings.RecordSystemAudio && _settings.RecordMicrophone)
        {
            ShowMessage(
                "Using standard MP4",
                "This hotkey recording has two separate audio tracks, so crash-safe MP4 is unavailable.",
                InfoBarSeverity.Warning);
        }

        object source = GetSelectedSource()!;
        _countdownCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _countdownCancellation.Token;

        try
        {
            int countdownSeconds = Math.Clamp(_settings.CountdownSeconds, 0, 5);
            if (countdownSeconds > 0)
            {
                SetUiState(RecordingUiState.Countdown);
                for (int remaining = countdownSeconds; remaining > 0; remaining--)
                {
                    StatusPillText.Text = $"Starting in {remaining}";
                    StartButtonLabel.Text = $"Cancel · recording starts in {remaining}";
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetUiState(RecordingUiState.Starting);

            // DWM can degrade WDA_EXCLUDEFROMCAPTURE to a black mask after a
            // hidden controller HWND is reused. A fresh, pre-excluded HWND for
            // every session preserves the clean first-recording behavior.
            CreateFreshRecordingOverlay();

            if (source is MonitorInfo)
            {
                TryApplyCaptureExclusion();
            }

            // Remove the control center from DWM composition before the capture
            // session begins; otherwise its exclusion mask can occupy initial frames.
            HideMainWindowToTray(showHint: false);

            Directory.CreateDirectory(_settings.OutputDirectory);
            string outputFile = CreateUniqueOutputPath(_settings.OutputDirectory);
            bool systemAudio = _settings.RecordSystemAudio;
            bool microphone = _settings.RecordMicrophone;

            await Task.Run(() =>
            {
                _graphicsDevice = new D3D11GraphicsDevice();
                _session = source switch
                {
                    MonitorInfo monitor => new RecordingSession(
                        _graphicsDevice, _settings, monitor, outputFile, _log, systemAudio, microphone),
                    CapturableWindow window => new RecordingSession(
                        _graphicsDevice, _settings, window, outputFile, _log, systemAudio, microphone),
                    _ => throw new InvalidOperationException("The selected capture source is no longer available."),
                };
                _session.Start();
            });

            _recordedElapsed = TimeSpan.Zero;
            _activeSegmentStartedUtc = DateTime.UtcNow;
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _statusTimer.Tick += OnStatusTimerTick;
            _statusTimer.Start();
            SetUiState(RecordingUiState.Recording);
            RefreshRecordingStatus();
            if (_stopAfterStartRequested)
            {
                _stopAfterStartRequested = false;
                await StopRecordingSafelyAsync();
            }
            else
            {
                ShowRecordingOverlay();
            }
        }
        catch (OperationCanceledException)
        {
            SetUiState(RecordingUiState.Idle);
        }
        catch (Exception exception)
        {
            _stopAfterStartRequested = false;
            _log.Error(exception, "Recording start failed");
            await FinalizeFailedStartAsync();
            SetUiState(RecordingUiState.Error);
            ShowMainWindow();
            ShowMessage("Couldn't start recording", FriendlyError(exception), InfoBarSeverity.Error);
            _trayIcon?.ShowNotification(
                "Recording could not start",
                FriendlyError(exception),
                TrayNotificationKind.Error);
        }
        finally
        {
            _countdownCancellation?.Dispose();
            _countdownCancellation = null;
        }
    }

    private string? ValidatePreflight()
    {
        object? source = GetSelectedSource();
        if (source is null)
        {
            return "Choose a screen or application window first.";
        }
        if (source is CapturableWindow window && !IsWindow(window.Handle))
        {
            PopulateSourcePicker();
            return "That window has closed. Choose another capture source.";
        }

        try
        {
            Directory.CreateDirectory(_settings.OutputDirectory);
            string writeProbe = Path.Combine(_settings.OutputDirectory, $".recorder-write-test-{Guid.NewGuid():N}.tmp");
            using (new FileStream(
                       writeProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 1, FileOptions.DeleteOnClose))
            {
            }

            string root = Path.GetPathRoot(Path.GetFullPath(_settings.OutputDirectory))!;
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < (long)_settings.MinFreeDiskGb * 1024 * 1024 * 1024)
            {
                return $"Only {drive.AvailableFreeSpace / 1_073_741_824d:0.0} GB is free. " +
                       $"Free at least {_settings.MinFreeDiskGb} GB or choose another folder.";
            }
        }
        catch (UnauthorizedAccessException)
        {
            return "The recordings folder is not writable. Choose another save location.";
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return "The recordings folder is unavailable. Choose another save location.";
        }

        return null;
    }

    private async Task<bool> ConfirmAudioCompatibilityAsync()
    {
        if (!_settings.CrashSafeContainer || !_settings.RecordSystemAudio || !_settings.RecordMicrophone)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = MainScroller.XamlRoot,
            Title = "Crash-safe mode needs one audio track",
            Content = "System audio and microphone are stored as separate editable tracks. " +
                      "With both enabled, this recording uses standard MP4 and cannot guarantee recovery after a power loss.",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string CreateUniqueOutputPath(string directory)
    {
        string baseName = $"recording-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        string path = Path.Combine(directory, baseName + ".mp4");
        for (int suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{baseName}-{suffix}.mp4");
        }
        return path;
    }

    private async Task FinalizeFailedStartAsync()
    {
        RecordingSession? session = _session;
        if (session is not null)
        {
            try
            {
                await Task.Run(session.Stop);
            }
            catch (Exception exception)
            {
                _log.Warning(exception, "Could not finalize partially started recording");
            }
        }
        CleanUpSession();
    }

    private void OnPauseButtonClick(object sender, RoutedEventArgs args) => TogglePauseResume();

    private void TogglePauseResume()
    {
        if (_session is null)
        {
            return;
        }

        if (_uiState == RecordingUiState.Recording)
        {
            _recordedElapsed += DateTime.UtcNow - _activeSegmentStartedUtc;
            _session.Pause();
            SetUiState(RecordingUiState.Paused);
        }
        else if (_uiState == RecordingUiState.Paused)
        {
            _session.Resume();
            _activeSegmentStartedUtc = DateTime.UtcNow;
            SetUiState(RecordingUiState.Recording);
        }

        RefreshRecordingStatus();
    }

    private async void OnStopButtonClick(object sender, RoutedEventArgs args) =>
        await StopRecordingSafelyAsync();

    private Task StopRecordingSafelyAsync()
    {
        if (_stopTask is not null)
        {
            return _stopTask;
        }

        _stopTask = StopRecordingCoreAsync();
        return _stopTask;
    }

    private async Task StopRecordingCoreAsync()
    {
        RecordingSession? session = _session;
        if (session is null)
        {
            _stopTask = null;
            return;
        }

        if (_uiState == RecordingUiState.Recording)
        {
            _recordedElapsed += DateTime.UtcNow - _activeSegmentStartedUtc;
        }

        _statusTimer?.Stop();
        SetUiState(RecordingUiState.Stopping);

        try
        {
            await Task.Run(session.Stop);

            string[] outputFiles = session.OutputFiles.ToArray();
            _latestOutputFile = outputFiles.LastOrDefault(File.Exists);
            long totalBytes = outputFiles.Where(File.Exists).Sum(path => new FileInfo(path).Length);
            string partText = outputFiles.Length > 1 ? $" · {outputFiles.Length} files" : string.Empty;
            string dropText = session.Statistics.FramesDropped == 0
                ? "No dropped frames"
                : $"{session.Statistics.FramesDropped} dropped frames";

            ResultHeadlineText.Text = session.AutoStopReason is null
                ? "Recording saved"
                : "Recording safely stopped";
            ResultDetailsText.Text =
                $"{FormatDuration(_recordedElapsed)} · {FormatBytes(totalBytes)}{partText} · {dropText}" +
                (session.AutoStopReason is string reason ? $"\n{reason}." : string.Empty);

            CleanUpSession();
            SetUiState(RecordingUiState.Idle);
            ResultPanel.Visibility = Visibility.Visible;

            if (session.AutoStopReason is null)
            {
                ShowMessage(
                    "Recording saved",
                    $"{Path.GetFileName(_latestOutputFile)} · {FormatDuration(_recordedElapsed)} · {FormatBytes(totalBytes)}",
                    InfoBarSeverity.Success);
                _trayIcon?.ShowNotification(
                    "Recording saved",
                    $"{Path.GetFileName(_latestOutputFile)} · {FormatDuration(_recordedElapsed)}");
            }
            DispatcherQueue.TryEnqueue(() => ResultPanel.StartBringIntoView());

            if (session.AutoStopReason is string autoStopReason)
            {
                ShowMessage("Recording stopped automatically", autoStopReason, InfoBarSeverity.Warning);
                _trayIcon?.ShowNotification(
                    "Recording stopped automatically",
                    autoStopReason,
                    TrayNotificationKind.Warning);
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Recording stop failed");
            CleanUpSession();
            SetUiState(RecordingUiState.Error);
            ShowMessage(
                "Recording needs attention",
                FriendlyError(exception) + " Any finalized video has been kept in your recordings folder.",
                InfoBarSeverity.Error);
            _trayIcon?.ShowNotification(
                "Recording needs attention",
                FriendlyError(exception),
                TrayNotificationKind.Error);
        }
        finally
        {
            _stopTask = null;
        }
    }

    private void OnStatusTimerTick(object? sender, object args)
    {
        if (_session is null)
        {
            return;
        }

        if (_session.HasFailed || _session.AutoStopReason is not null)
        {
            _statusTimer?.Stop();
            _ = StopRecordingSafelyAsync();
            return;
        }

        RefreshRecordingStatus();
    }

    private void RefreshRecordingStatus()
    {
        if (_session is null)
        {
            return;
        }

        TimeSpan elapsed = CurrentRecordedElapsed();

        RecordingTimerText.Text = FormatDuration(elapsed);
        long dropped = _session.Statistics.FramesDropped;
        RecordingStatsText.Text = dropped == 0
            ? $"{_session.Statistics.FramesWritten:N0} frames written · No dropped frames"
            : $"{_session.Statistics.FramesWritten:N0} frames written · {dropped:N0} dropped";
        UpdatePresentationSurfaces();
    }

    private TimeSpan CurrentRecordedElapsed() =>
        _uiState == RecordingUiState.Recording
            ? _recordedElapsed + (DateTime.UtcNow - _activeSegmentStartedUtc)
            : _recordedElapsed;

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 100
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : duration.ToString(@"hh\:mm\:ss");

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824)
        {
            return $"{bytes / 1_073_741_824d:0.##} GB";
        }
        return $"{bytes / 1_048_576d:0.0} MB";
    }

    private void SetUiState(RecordingUiState state)
    {
        _uiState = state;
        bool configurable = state is RecordingUiState.Idle or RecordingUiState.Error;
        bool showingIdleTransport = state is RecordingUiState.Idle or RecordingUiState.Error or
            RecordingUiState.Countdown or RecordingUiState.Starting;
        bool showingRecordingTransport = state is RecordingUiState.Recording or RecordingUiState.Paused or
            RecordingUiState.Stopping;

        SourcePicker.IsEnabled = configurable;
        RefreshSourcesButton.IsEnabled = configurable;
        ChooseFolderButton.IsEnabled = configurable;
        QualityPresetPicker.IsEnabled = configurable;
        SystemAudioToggle.IsEnabled = configurable;
        MicrophoneToggle.IsEnabled = configurable;
        SystemVolumeSlider.IsEnabled = configurable && SystemAudioToggle.IsOn;
        MicrophoneVolumeSlider.IsEnabled = configurable && MicrophoneToggle.IsOn;
        AdvancedExpander.IsEnabled = configurable;
        QualityLevelSlider.IsEnabled = configurable &&
            (RateControlPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "cq";

        IdleTransportPanel.Visibility = showingIdleTransport ? Visibility.Visible : Visibility.Collapsed;
        RecordingPanel.Visibility = showingRecordingTransport ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = state is RecordingUiState.Idle or RecordingUiState.Error or RecordingUiState.Countdown;
        PauseButton.IsEnabled = state is RecordingUiState.Recording or RecordingUiState.Paused;
        StopButton.IsEnabled = state is RecordingUiState.Recording or RecordingUiState.Paused;

        switch (state)
        {
            case RecordingUiState.Idle:
                Title = "Screen Recorder";
                StatusPillText.Text = "Ready";
                StartButtonLabel.Text = "Start recording";
                break;
            case RecordingUiState.Error:
                Title = "Screen Recorder";
                StatusPillText.Text = "Needs attention";
                StartButtonLabel.Text = "Try again";
                break;
            case RecordingUiState.Countdown:
                Title = "Starting — Screen Recorder";
                StatusPillText.Text = "Starting";
                StartButtonLabel.Text = "Cancel countdown";
                break;
            case RecordingUiState.Starting:
                Title = "Preparing — Screen Recorder";
                StatusPillText.Text = "Preparing";
                StartButtonLabel.Text = "Preparing recorder…";
                break;
            case RecordingUiState.Recording:
                Title = "● Recording — Screen Recorder";
                StatusPillText.Text = "Recording";
                RecordingStateText.Text = "Recording";
                PauseButtonLabel.Text = "Pause";
                PauseButtonIcon.Symbol = Symbol.Pause;
                break;
            case RecordingUiState.Paused:
                Title = "Paused — Screen Recorder";
                StatusPillText.Text = "Paused";
                RecordingStateText.Text = "Paused";
                PauseButtonLabel.Text = "Resume";
                PauseButtonIcon.Symbol = Symbol.Play;
                break;
            case RecordingUiState.Stopping:
                Title = "Saving — Screen Recorder";
                StatusPillText.Text = "Saving";
                RecordingStateText.Text = "Finalizing recording…";
                break;
        }

        if (state is RecordingUiState.Idle or RecordingUiState.Error)
        {
            _recordingOverlay?.HideController();
        }
        UpdatePresentationSurfaces();
    }

    private void UpdatePresentationSurfaces()
    {
        RecorderPresentationState presentationState = _uiState switch
        {
            RecordingUiState.Idle => RecorderPresentationState.Ready,
            RecordingUiState.Countdown => RecorderPresentationState.Countdown,
            RecordingUiState.Starting => RecorderPresentationState.Starting,
            RecordingUiState.Recording => RecorderPresentationState.Recording,
            RecordingUiState.Paused => RecorderPresentationState.Paused,
            RecordingUiState.Stopping => RecorderPresentationState.Stopping,
            RecordingUiState.Error => RecorderPresentationState.Error,
            _ => RecorderPresentationState.Ready,
        };
        TimeSpan elapsed = CurrentRecordedElapsed();
        long droppedFrames = _session?.Statistics.FramesDropped ?? 0;
        _recordingOverlay?.Update(presentationState, elapsed, droppedFrames);
        _trayIcon?.Update(
            presentationState,
            elapsed,
            _recordingOverlay?.IsControllerVisible == true);
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }

    private static string FriendlyError(Exception exception)
    {
        Exception root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return root switch
        {
            UnauthorizedAccessException => "Screen recording or folder access was denied. Check Windows privacy settings and the save location.",
            IOException => "The recording file could not be written. Check the save location and available disk space.",
            COMException => "Windows could not start the selected capture or hardware encoder. Try H.264, a lower frame rate, or another source.",
            _ => "The recorder could not complete that operation. Try another source or quality profile.",
        };
    }

    private void CleanUpSession()
    {
        if (_statusTimer is not null)
        {
            _statusTimer.Tick -= OnStatusTimerTick;
            _statusTimer.Stop();
        }
        _statusTimer = null;
        _session?.Dispose();
        _session = null;
        _graphicsDevice?.Dispose();
        _graphicsDevice = null;
        DisposeRecordingOverlay();

        if (_captureExclusionApplied)
        {
            SetWindowDisplayAffinity(WindowNative.GetWindowHandle(this), 0);
        }
        _captureExclusionApplied = false;
        _captureExclusionAttempted = false;
    }

    private void OnPlayLatestClick(object sender, RoutedEventArgs args)
    {
        if (_latestOutputFile is null || !File.Exists(_latestOutputFile))
        {
            ShowMessage("Video unavailable", "The recording file could not be found.", InfoBarSeverity.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _latestOutputFile,
            UseShellExecute = true,
        });
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs args)
    {
        Directory.CreateDirectory(_settings.OutputDirectory);
        if (_latestOutputFile is not null && File.Exists(_latestOutputFile))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_latestOutputFile}\"",
                UseShellExecute = true,
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _settings.OutputDirectory,
            UseShellExecute = true,
        });
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs args)
    {
        if (_latestOutputFile is null)
        {
            return;
        }

        var data = new DataPackage();
        data.SetText(_latestOutputFile);
        Clipboard.SetContent(data);
        ShowMessage("Path copied", "The recording path is ready to paste.", InfoBarSeverity.Success);
    }

    private void RegisterGlobalHotkeys(bool showFeedback)
    {
        DisposeGlobalHotkeys();
        var unavailable = new List<string>();

        try
        {
            _startStopHotkey = new GlobalHotkey(
                _settings.StartStopHotkey,
                () => DispatcherQueue.TryEnqueue(() => _ = HandleGlobalStartStopAsync()));
            if (!_startStopHotkey.RegistrationSucceeded)
            {
                unavailable.Add($"Start/stop ({_settings.StartStopHotkey})");
            }
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Could not configure start/stop hotkey {Gesture}", _settings.StartStopHotkey);
            unavailable.Add($"Start/stop ({_settings.StartStopHotkey})");
        }

        try
        {
            _pauseResumeHotkey = new GlobalHotkey(
                _settings.PauseResumeHotkey,
                () => DispatcherQueue.TryEnqueue(HandleGlobalPauseResume));
            if (!_pauseResumeHotkey.RegistrationSucceeded)
            {
                unavailable.Add($"Pause/resume ({_settings.PauseResumeHotkey})");
            }
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Could not configure pause/resume hotkey {Gesture}", _settings.PauseResumeHotkey);
            unavailable.Add($"Pause/resume ({_settings.PauseResumeHotkey})");
        }

        if (showFeedback && unavailable.Count > 0)
        {
            ShowMessage(
                "Global hotkey unavailable",
                string.Join(" and ", unavailable) +
                " is already used by Windows or another app. Choose a different shortcut under Advanced.",
                InfoBarSeverity.Warning);
        }
        RefreshReadySummary();
    }

    private async Task HandleGlobalStartStopAsync()
    {
        if (_closeRequested)
        {
            return;
        }

        switch (_uiState)
        {
            case RecordingUiState.Idle:
            case RecordingUiState.Error:
                await StartRecordingSafelyAsync(triggeredByHotkey: true);
                break;
            case RecordingUiState.Countdown:
                _countdownCancellation?.Cancel();
                break;
            case RecordingUiState.Starting:
                _stopAfterStartRequested = true;
                StatusPillText.Text = "Stop requested";
                break;
            case RecordingUiState.Recording:
            case RecordingUiState.Paused:
                await StopRecordingSafelyAsync();
                break;
        }
    }

    private void HandleGlobalPauseResume()
    {
        if (!_closeRequested && _uiState is RecordingUiState.Recording or RecordingUiState.Paused)
        {
            TogglePauseResume();
        }
    }

    private void DisposeGlobalHotkeys()
    {
        _startStopHotkey?.Dispose();
        _startStopHotkey = null;
        _pauseResumeHotkey?.Dispose();
        _pauseResumeHotkey = null;
    }

    private void ShowMainWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        if (_mainWindowHiddenToTray)
        {
            AppWindow.Show();
        }
        Activate();
        _mainWindowHiddenToTray = false;
    }

    private void HideMainWindowToTray(bool showHint)
    {
        if (!_mainWindowHiddenToTray)
        {
            AppWindow.Hide();
        }
        _mainWindowHiddenToTray = true;

        if (showHint && !_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon?.ShowNotification(
                "Screen Recorder is still running",
                "Use the notification-area icon or your global hotkeys to open and control it.");
        }
    }

    private void ShowRecordingOverlay()
    {
        if (_uiState is not (RecordingUiState.Recording or RecordingUiState.Paused))
        {
            return;
        }

        _recordingOverlay?.ShowController();
        UpdatePresentationSurfaces();
    }

    private void HideRecordingOverlay()
    {
        _recordingOverlay?.HideController();
        UpdatePresentationSurfaces();
    }

    private void ToggleRecordingOverlay()
    {
        if (_uiState is not (RecordingUiState.Recording or RecordingUiState.Paused))
        {
            return;
        }

        if (_recordingOverlay?.IsControllerVisible == true)
        {
            HideRecordingOverlay();
        }
        else
        {
            ShowRecordingOverlay();
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        _countdownCancellation?.Cancel();

        if (_startTask is not null)
        {
            await _startTask;
        }
        if (_session is not null)
        {
            await StopRecordingSafelyAsync();
        }

        _allowClose = true;
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _settingsSaveTimer?.Stop();
        DisposeGlobalHotkeys();
        DisposeRecordingOverlay();
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void TryApplyCaptureExclusion()
    {
        if (_captureExclusionAttempted)
        {
            return;
        }

        _captureExclusionAttempted = true;
        try
        {
            _captureExclusionApplied = SetWindowDisplayAffinity(
                WindowNative.GetWindowHandle(this), WdaExcludeFromCapture);
            if (!_captureExclusionApplied)
            {
                _log.Warning("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) was rejected");
                DispatcherQueue.TryEnqueue(() => ShowMessage(
                    "Recorder window may be visible",
                    "Windows could not hide this control window from the recording. Minimize it while capturing.",
                    InfoBarSeverity.Warning));
            }
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Could not exclude recorder window from display capture");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HideMainWindowToTray(showHint: true);
    }
}
