using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Recorder.App;

/// <summary>
/// Compact recording transport. It is always on top, absent from Alt+Tab/taskbar,
/// and excluded from Windows capture so it never appears in this recorder's output.
/// </summary>
public sealed partial class RecordingOverlayWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private Action _pauseResumeRequested = () => { };
    private Action _stopRequested = () => { };
    private Action _hideRequested = () => { };
    private bool _captureExclusionApplied;
    private uint? _dragPointerId;
    private NativePoint _dragStartCursor;
    private PointInt32 _dragStartWindowPosition;

    public bool IsControllerVisible { get; private set; }

    public RecordingOverlayWindow()
    {
        InitializeComponent();
        Title = "Recording controls";
        AppWindow.Resize(new SizeInt32(430, 78));
        AppWindow.IsShownInSwitchers = false;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // The HWND exists before the controller is ever shown. Apply affinity here so
        // no captured frame can observe the window before exclusion takes effect.
        ApplyCaptureExclusion();
        Activated += OnActivated;
    }

    public void ConfigureActions(
        Action pauseResumeRequested,
        Action stopRequested,
        Action hideRequested)
    {
        _pauseResumeRequested = pauseResumeRequested;
        _stopRequested = stopRequested;
        _hideRequested = hideRequested;
    }

    public void ShowController()
    {
        if (!IsControllerVisible)
        {
            Activate();
            PositionAtTopRight();
            IsControllerVisible = true;
        }
        else
        {
            AppWindow.Show();
        }
    }

    public void HideController()
    {
        if (IsControllerVisible)
        {
            AppWindow.Hide();
        }
        IsControllerVisible = false;
    }

    internal void Update(RecorderPresentationState state, TimeSpan elapsed, long droppedFrames)
    {
        string formattedDuration = FormatDuration(elapsed);
        OverlayTimerText.Text = formattedDuration;
        AutomationProperties.SetName(
            OverlayTimerText,
            $"Recording duration {formattedDuration}");
        OverlayHealthText.Text = droppedFrames == 0
            ? "No dropped frames"
            : $"{droppedFrames:N0} dropped frames";

        switch (state)
        {
            case RecorderPresentationState.Recording:
                OverlayStateText.Text = "REC";
                OverlayStateText.Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["RecorderRecordingBrush"];
                OverlayPauseIcon.Symbol = Symbol.Pause;
                OverlayPauseButton.IsEnabled = true;
                AutomationProperties.SetName(OverlayPauseButton, "Pause recording");
                OverlayStopButton.IsEnabled = true;
                break;
            case RecorderPresentationState.Paused:
                OverlayStateText.Text = "Paused";
                OverlayStateText.Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["RecorderMutedBrush"];
                OverlayPauseIcon.Symbol = Symbol.Play;
                OverlayPauseButton.IsEnabled = true;
                AutomationProperties.SetName(OverlayPauseButton, "Resume recording");
                OverlayStopButton.IsEnabled = true;
                break;
            case RecorderPresentationState.Stopping:
                OverlayStateText.Text = "Saving…";
                OverlayPauseButton.IsEnabled = false;
                OverlayStopButton.IsEnabled = false;
                break;
        }
    }

    public void CloseController()
    {
        IsControllerVisible = false;
        Close();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Safety net for a constructor-time failure only. Never cycle the affinity
        // here: Activated fires on every focus change while the controller is
        // visible, and the None→Exclude transition below has a moment where a
        // captured frame could see the controller.
        if (!_captureExclusionApplied)
        {
            ApplyCaptureExclusion();
        }
    }

    /// <summary>
    /// Applies exclusion before this controller is shown. The controller HWND is
    /// intentionally replaced between recording sessions rather than reused.
    /// </summary>
    private void ApplyCaptureExclusion()
    {
        IntPtr windowHandle = WindowNative.GetWindowHandle(this);
        _captureExclusionApplied = SetWindowDisplayAffinity(windowHandle, WdaExcludeFromCapture);
    }

    private void PositionAtTopRight()
    {
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = display.WorkArea;
        SizeInt32 size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            workArea.X + Math.Max(12, workArea.Width - size.Width - 18),
            workArea.Y + 18));
    }

    private void OnControllerPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (IsInsideButton(args.OriginalSource as DependencyObject) ||
            !args.GetCurrentPoint(ControllerSurface).Properties.IsLeftButtonPressed ||
            !GetCursorPos(out _dragStartCursor))
        {
            return;
        }

        _dragPointerId = args.Pointer.PointerId;
        _dragStartWindowPosition = AppWindow.Position;
        ControllerSurface.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null && element != ControllerSurface)
        {
            if (element is ButtonBase)
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnControllerPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointerId != args.Pointer.PointerId ||
            !args.GetCurrentPoint(ControllerSurface).Properties.IsLeftButtonPressed ||
            !GetCursorPos(out NativePoint cursor))
        {
            return;
        }

        AppWindow.Move(new PointInt32(
            _dragStartWindowPosition.X + cursor.X - _dragStartCursor.X,
            _dragStartWindowPosition.Y + cursor.Y - _dragStartCursor.Y));
        args.Handled = true;
    }

    private void OnControllerPointerReleased(object sender, PointerRoutedEventArgs args) =>
        EndPointerDrag(args);

    private void OnControllerPointerCanceled(object sender, PointerRoutedEventArgs args) =>
        EndPointerDrag(args);

    private void OnControllerPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointerId == args.Pointer.PointerId)
        {
            _dragPointerId = null;
        }
    }

    private void EndPointerDrag(PointerRoutedEventArgs args)
    {
        if (_dragPointerId != args.Pointer.PointerId)
        {
            return;
        }

        _dragPointerId = null;
        ControllerSurface.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void OnPauseClick(object sender, RoutedEventArgs args) => _pauseResumeRequested();

    private void OnStopClick(object sender, RoutedEventArgs args) => _stopRequested();

    private void OnHideClick(object sender, RoutedEventArgs args) => _hideRequested();

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 100
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : duration.ToString(@"hh\:mm\:ss");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
