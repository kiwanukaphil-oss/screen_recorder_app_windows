using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Recorder.App;

internal enum RecorderPresentationState
{
    Ready,
    Countdown,
    Starting,
    Recording,
    Paused,
    Stopping,
    Error,
}

internal enum TrayNotificationKind
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Native Windows notification-area icon. It owns a message-only window on a
/// dedicated thread, avoiding a Windows Forms runtime dependency in the WinUI app.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint TrayCallbackMessage = WmApp + 1;
    private const uint DisposeMessage = WmApp + 2;
    private const uint WmCommand = 0x0111;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint WmNull = 0x0000;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0;
    private const uint MfDisabled = 0x2;
    private const uint MfChecked = 0x8;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCommand = 0x100;

    private const uint OpenCommand = 1001;
    private const uint StartStopCommand = 1002;
    private const uint PauseResumeCommand = 1003;
    private const uint OverlayCommand = 1004;
    private const uint ExitCommand = 1005;

    private static readonly ConcurrentDictionary<IntPtr, TrayIconService> Instances = new();
    private static readonly WindowProcedure SharedWindowProcedure = WindowProc;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _messageThread;
    private readonly object _stateLock = new();
    private RecorderPresentationState _state = RecorderPresentationState.Ready;
    private TimeSpan _elapsed;
    private bool _overlayVisible;
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private uint _taskbarCreatedMessage;
    private bool _ownsIcon;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? StartStopRequested;
    public event Action? PauseResumeRequested;
    public event Action? ToggleOverlayRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "Screen Recorder notification area",
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The notification-area icon could not be initialized.");
        }
    }

    public void Update(RecorderPresentationState state, TimeSpan elapsed, bool overlayVisible)
    {
        lock (_stateLock)
        {
            _state = state;
            _elapsed = elapsed;
            _overlayVisible = overlayVisible;
        }

        if (_windowHandle != IntPtr.Zero)
        {
            NotifyIconData data = CreateNotifyIconData(NifTip | NifShowTip);
            data.ToolTip = TooltipFor(state, elapsed);
            ShellNotifyIcon(NimModify, ref data);
        }
    }

    public void ShowNotification(
        string title,
        string message,
        TrayNotificationKind kind = TrayNotificationKind.Info)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        NotifyIconData data = CreateNotifyIconData(NifInfo);
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(message, 255);
        data.InfoFlags = (uint)kind;
        ShellNotifyIcon(NimModify, ref data);
    }

    private void MessageLoop()
    {
        string className = $"RecorderTrayWindow_{Guid.NewGuid():N}";
        IntPtr module = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Instance = module,
            ClassName = className,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(SharedWindowProcedure),
        };

        ushort atom = RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            _ready.Set();
            return;
        }

        try
        {
            _windowHandle = CreateWindowEx(
                0, className, "Screen Recorder", 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, module, IntPtr.Zero);
            if (_windowHandle == IntPtr.Zero)
            {
                _ready.Set();
                return;
            }

            Instances[_windowHandle] = this;
            _iconHandle = LoadApplicationIcon(out _ownsIcon);
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            if (!AddNotificationIcon())
            {
                Instances.TryRemove(_windowHandle, out _);
                DestroyWindow(_windowHandle);
                _windowHandle = IntPtr.Zero;
                _ready.Set();
                return;
            }

            _ready.Set();

            while (GetMessage(out Message message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            _ready.Set();
            if (_windowHandle != IntPtr.Zero)
            {
                NotifyIconData data = CreateNotifyIconData(0);
                ShellNotifyIcon(NimDelete, ref data);
                Instances.TryRemove(_windowHandle, out _);
                _windowHandle = IntPtr.Zero;
            }
            if (_ownsIcon && _iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
            }
            UnregisterClass(className, module);
        }
    }

    private bool AddNotificationIcon()
    {
        RecorderPresentationState state;
        TimeSpan elapsed;
        lock (_stateLock)
        {
            state = _state;
            elapsed = _elapsed;
        }

        NotifyIconData data = CreateNotifyIconData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.CallbackMessage = TrayCallbackMessage;
        data.Icon = _iconHandle;
        data.ToolTip = TooltipFor(state, elapsed);
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            return false;
        }

        data = CreateNotifyIconData(0);
        data.VersionOrTimeout = NotifyIconVersion4;
        ShellNotifyIcon(NimSetVersion, ref data);
        return true;
    }

    private void HandleTrayCallback(IntPtr wParam, IntPtr lParam)
    {
        uint notification = (uint)(lParam.ToInt64() & 0xFFFF);
        if (notification is WmLButtonDoubleClick or NinSelect or NinKeySelect)
        {
            OpenRequested?.Invoke();
            return;
        }

        if (notification == WmContextMenu)
        {
            ShowContextMenu(wParam);
        }
    }

    private void ShowContextMenu(IntPtr anchorCoordinates)
    {
        RecorderPresentationState state;
        bool overlayVisible;
        lock (_stateLock)
        {
            state = _state;
            overlayVisible = _overlayVisible;
        }

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, OpenCommand, "Open Screen Recorder");
            SetMenuDefaultItem(menu, OpenCommand, false);
            AppendMenu(menu, MfSeparator, 0, null);

            string startStopText = state switch
            {
                RecorderPresentationState.Countdown => "Cancel recording start",
                RecorderPresentationState.Starting => "Stop when ready",
                RecorderPresentationState.Recording or RecorderPresentationState.Paused => "Stop recording",
                RecorderPresentationState.Stopping => "Saving recording…",
                _ => "Start recording",
            };
            uint startStopFlags = state == RecorderPresentationState.Stopping ? MfDisabled : MfString;
            AppendMenu(menu, startStopFlags, StartStopCommand, startStopText);

            bool canPause = state is RecorderPresentationState.Recording or RecorderPresentationState.Paused;
            string pauseText = state == RecorderPresentationState.Paused ? "Resume recording" : "Pause recording";
            AppendMenu(menu, canPause ? MfString : MfDisabled, PauseResumeCommand, pauseText);

            uint overlayFlags = canPause ? MfString : MfDisabled;
            if (overlayVisible)
            {
                overlayFlags |= MfChecked;
            }
            AppendMenu(menu, overlayFlags, OverlayCommand, "Show recording controller");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, "Exit");

            int x = unchecked((short)(anchorCoordinates.ToInt64() & 0xFFFF));
            int y = unchecked((short)((anchorCoordinates.ToInt64() >> 16) & 0xFFFF));
            if (x == -1 && y == -1)
            {
                GetCursorPos(out Point cursor);
                x = cursor.X;
                y = cursor.Y;
            }

            SetForegroundWindow(_windowHandle);
            uint command = TrackPopupMenu(
                menu, TpmRightButton | TpmReturnCommand, x, y, 0, _windowHandle, IntPtr.Zero);
            DispatchCommand(command);
            PostMessage(_windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void DispatchCommand(uint command)
    {
        switch (command)
        {
            case OpenCommand:
                OpenRequested?.Invoke();
                break;
            case StartStopCommand:
                StartStopRequested?.Invoke();
                break;
            case PauseResumeCommand:
                PauseResumeRequested?.Invoke();
                break;
            case OverlayCommand:
                ToggleOverlayRequested?.Invoke();
                break;
            case ExitCommand:
                ExitRequested?.Invoke();
                break;
        }
    }

    private NotifyIconData CreateNotifyIconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = 1,
        Flags = flags,
    };

    private static string TooltipFor(RecorderPresentationState state, TimeSpan elapsed) => state switch
    {
        RecorderPresentationState.Ready => "Screen Recorder — Ready",
        RecorderPresentationState.Error => "Screen Recorder — Needs attention",
        RecorderPresentationState.Countdown => "Screen Recorder — Starting",
        RecorderPresentationState.Starting => "Screen Recorder — Preparing",
        RecorderPresentationState.Recording => $"Recording — {FormatDuration(elapsed)}",
        RecorderPresentationState.Paused => $"Paused — {FormatDuration(elapsed)}",
        RecorderPresentationState.Stopping => "Screen Recorder — Saving",
        _ => "Screen Recorder",
    };

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalHours >= 100
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : elapsed.ToString(@"hh\:mm\:ss");

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static IntPtr LoadApplicationIcon(out bool ownsIcon)
    {
        string? path = Environment.ProcessPath;
        if (path is not null && ExtractIconEx(path, 0, out IntPtr large, out IntPtr small, 1) > 0)
        {
            if (small != IntPtr.Zero)
            {
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }
                ownsIcon = true;
                return small;
            }
            if (large != IntPtr.Zero)
            {
                ownsIcon = true;
                return large;
            }
        }

        ownsIcon = false;
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(window, out TrayIconService? service))
        {
            if (message == service._taskbarCreatedMessage)
            {
                service.AddNotificationIcon();
                return IntPtr.Zero;
            }
            if (message == TrayCallbackMessage)
            {
                service.HandleTrayCallback(wParam, lParam);
                return IntPtr.Zero;
            }
            if (message == WmCommand)
            {
                service.DispatchCommand((uint)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;
            }
            if (message == DisposeMessage)
            {
                NotifyIconData data = service.CreateNotifyIconData(0);
                ShellNotifyIcon(NimDelete, ref data);
                Instances.TryRemove(window, out _);
                DestroyWindow(window);
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        IntPtr window = _windowHandle;
        if (window != IntPtr.Zero)
        {
            PostMessage(window, DisposeMessage, IntPtr.Zero, IntPtr.Zero);
            _messageThread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ToolTip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Value;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) =>
        Shell_NotifyIcon(message, ref data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        out IntPtr largeIcon,
        out IntPtr smallIcon,
        uint iconCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint identifier, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, bool byPosition);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr window,
        IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
