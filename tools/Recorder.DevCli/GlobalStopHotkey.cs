using System.Runtime.InteropServices;

namespace Recorder.DevCli;

/// <summary>
/// System-wide Ctrl+Shift+F9 hotkey for stopping a recording while another app (or a
/// fullscreen game) has focus. RegisterHotKey requires a thread with a message loop,
/// which console apps lack — so this runs a dedicated loop thread. The production app
/// will get a configurable hotkey service; this proves the mechanism for M1.
/// </summary>
public sealed class GlobalStopHotkey : IDisposable
{
    private const int ModControl = 0x2;
    private const int ModShift = 0x4;
    private const int VkF9 = 0x78;
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const int HotkeyId = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly Thread _messageLoopThread;
    private uint _loopThreadId;
    public bool RegistrationSucceeded { get; private set; }

    /// <summary>
    /// Spawns the message-loop thread: register the hotkey (must happen on the same
    /// thread that pumps messages), pump until WM_QUIT, invoke the callback on each
    /// WM_HOTKEY, unregister on the way out.
    /// </summary>
    public GlobalStopHotkey(Action onHotkeyPressed)
    {
        using var registrationDone = new ManualResetEventSlim();

        _messageLoopThread = new Thread(() =>
        {
            _loopThreadId = GetCurrentThreadId();
            RegistrationSucceeded = RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModShift, VkF9);
            registrationDone.Set();

            while (GetMessageW(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmHotkey)
                {
                    onHotkeyPressed();
                }
            }

            if (RegistrationSucceeded)
            {
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
            }
        })
        {
            Name = "HotkeyMessageLoop",
            IsBackground = true,
        };

        _messageLoopThread.Start();
        registrationDone.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_loopThreadId != 0)
        {
            PostThreadMessageW(_loopThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _messageLoopThread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
