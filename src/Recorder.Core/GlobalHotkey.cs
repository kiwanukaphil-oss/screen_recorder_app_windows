using System.Runtime.InteropServices;

namespace Recorder.Core;

/// <summary>
/// Registers one configurable system-wide hotkey on a dedicated Win32 message-loop
/// thread. The owner decides what the gesture does and keeps this object alive for as
/// long as the gesture should remain available.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int ModAlt = 0x1;
    private const int ModControl = 0x2;
    private const int ModShift = 0x4;
    private const int ModWindows = 0x8;
    private const int ModNoRepeat = 0x4000;
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

    [DllImport("user32.dll", SetLastError = true)]
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
    private int _disposed;

    public bool RegistrationSucceeded { get; private set; }
    public int RegistrationErrorCode { get; private set; }
    public string Gesture { get; }

    /// <summary>
    /// Spawns the message-loop thread, registers the requested gesture on that thread,
    /// and pumps until disposal. Supported keys are A-Z, 0-9, and F1-F24.
    /// </summary>
    public GlobalHotkey(string gesture, Action onHotkeyPressed)
    {
        ArgumentNullException.ThrowIfNull(onHotkeyPressed);
        if (!TryParseGesture(gesture, out uint modifiers, out uint virtualKey))
        {
            throw new ArgumentException($"Unsupported hotkey gesture '{gesture}'.", nameof(gesture));
        }

        Gesture = gesture;
        using var registrationDone = new ManualResetEventSlim();

        _messageLoopThread = new Thread(() =>
        {
            _loopThreadId = GetCurrentThreadId();
            RegistrationSucceeded = RegisterHotKey(
                IntPtr.Zero, HotkeyId, modifiers | ModNoRepeat, virtualKey);
            if (!RegistrationSucceeded)
            {
                RegistrationErrorCode = Marshal.GetLastWin32Error();
            }
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
            Name = $"GlobalHotkey:{gesture}",
            IsBackground = true,
        };

        _messageLoopThread.Start();
        registrationDone.Wait(TimeSpan.FromSeconds(2));
    }

    internal static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        foreach (string rawPart in gesture.Split(
                     '+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    continue;
                case "SHIFT":
                    modifiers |= ModShift;
                    continue;
                case "ALT":
                    modifiers |= ModAlt;
                    continue;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWindows;
                    continue;
            }

            if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
            {
                virtualKey = part[0];
                continue;
            }

            if (part.Length >= 2 && part[0] == 'F' &&
                int.TryParse(part[1..], out int functionKey) && functionKey is >= 1 and <= 24)
            {
                virtualKey = (uint)(0x70 + functionKey - 1);
                continue;
            }

            return false;
        }

        return modifiers != 0 && virtualKey != 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_loopThreadId != 0)
        {
            PostThreadMessageW(_loopThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _messageLoopThread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
