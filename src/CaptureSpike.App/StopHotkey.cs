using System.Runtime.InteropServices;

namespace CaptureSpike.App;

/// <summary>
/// Global F9 hotkey that stops the recording while the app window is minimized.
/// Windows uses RegisterHotKey with a message-only window on a dedicated
/// thread (no DLL hook, no message-loop thread requirement);
/// Linux/X11 grabs F9 on the root window from a background event thread.
/// </summary>
public static class StopHotkey
{
    public static void Install(Action onF9, Action<string>? log = null)
    {
        if (OperatingSystem.IsWindows()) WinHotkey.Install(onF9, log);
        else if (OperatingSystem.IsLinux()) X11Grab.Install(onF9);
        else throw new PlatformNotSupportedException(
            "The global stop hotkey is supported on Windows and Linux/X11.");
    }

    public static void Uninstall()
    {
        if (OperatingSystem.IsWindows()) WinHotkey.Uninstall();
        else if (OperatingSystem.IsLinux()) X11Grab.Uninstall();
    }

    // ----- Windows: RegisterHotKey + message-only window -----

    private static class WinHotkey
    {
        private const int WM_HOTKEY = 0x0312;
        private const int WM_QUIT = 0x0012;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_F9 = 0x79;
        private const int HOTKEY_ID = 1;
        private static readonly IntPtr HWND_MESSAGE = new(-3);
        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly object Gate = new();
        private static Thread? _thread;
        private static IntPtr _hwnd;
        private static Action? _onF9;

        public static void Install(Action onF9, Action<string>? log)
        {
            lock (Gate)
            {
                if (_thread is not null) throw new InvalidOperationException(
                    "The stop hotkey is already installed.");
                var ready = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _onF9 = onF9;
                _thread = new Thread(() => MessageLoop(ready, log))
                {
                    IsBackground = true,
                    Name = "F9 stop hotkey",
                };
                _thread.Start();
                if (!ready.Task.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out installing the F9 stop hotkey.");
                ready.Task.GetAwaiter().GetResult(); // rethrows install failures
            }
        }

        public static void Uninstall()
        {
            IntPtr hwnd;
            Thread? thread;
            lock (Gate)
            {
                hwnd = _hwnd;
                _hwnd = IntPtr.Zero;
                thread = _thread;
                _thread = null;
                _onF9 = null;
            }
            if (hwnd != IntPtr.Zero)
                PostMessageW(hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread?.Join(TimeSpan.FromSeconds(2));
        }

        private static void MessageLoop(TaskCompletionSource<bool> ready, Action<string>? log)
        {
            var hwnd = IntPtr.Zero;
            try
            {
                // Message-only window: exists solely to receive WM_HOTKEY.
                hwnd = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0,
                    HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"Could not create the hotkey window (error {Marshal.GetLastWin32Error()}).");
                lock (Gate) { _hwnd = hwnd; }
                if (!RegisterHotKey(hwnd, HOTKEY_ID, MOD_NOREPEAT, VK_F9))
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        err == ERROR_HOTKEY_ALREADY_REGISTERED
                            ? "F9 is already registered as a global hotkey by another app."
                            : $"Could not register the F9 hotkey (error {err}).");
                }
                log?.Invoke("F9 stop hotkey installed.");
                ready.TrySetResult(true);

                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
                {
                    if (msg.message == WM_HOTKEY && msg.wParam == (IntPtr)HOTKEY_ID)
                    {
                        log?.Invoke("F9 pressed.");
                        _onF9?.Invoke();
                    }
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
            finally
            {
                if (hwnd != IntPtr.Zero)
                {
                    UnregisterHotKey(hwnd, HOTKEY_ID);
                    DestroyWindow(hwnd);
                }
                lock (Gate) { if (_hwnd == hwnd) _hwnd = IntPtr.Zero; }
            }
        }
    }

    // ----- Linux/X11: XGrabKey on the root window -----

    private static class X11Grab
    {
        private const int KeyPress = 2;
        private const ulong XK_F9 = 0xFFC6;
        private const uint Mod2Mask = 16; // num lock
        private const int GrabModeAsync = 1;

        // XEvent is a 192-byte union on 64-bit; we only read the type field.
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        private struct XEvent
        {
            [FieldOffset(0)] public int type;
        }

        [DllImport("libX11")]
        private static extern IntPtr XOpenDisplay(string? display);
        [DllImport("libX11")]
        private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11")]
        private static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport("libX11")]
        private static extern int XKeysymToKeycode(IntPtr display, ulong keysym);
        [DllImport("libX11")]
        private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers,
            IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode);
        [DllImport("libX11")]
        private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers,
            IntPtr grabWindow);
        [DllImport("libX11")]
        private static extern int XPending(IntPtr display);
        [DllImport("libX11")]
        private static extern int XNextEvent(IntPtr display, out XEvent ev);
        [DllImport("libX11")]
        private static extern int XFlush(IntPtr display);

        private static readonly object Gate = new();
        private static Thread? _thread;
        private static volatile bool _running;
        private static Action? _onF9;

        public static void Install(Action onF9)
        {
            lock (Gate)
            {
                if (_thread is not null) throw new InvalidOperationException(
                    "The stop hotkey is already installed.");
                var ready = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _onF9 = onF9;
                _running = true;
                _thread = new Thread(() => EventLoop(ready))
                {
                    IsBackground = true,
                    Name = "F9 stop hotkey",
                };
                _thread.Start();
                if (!ready.Task.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out installing the F9 stop hotkey.");
                ready.Task.GetAwaiter().GetResult(); // rethrows install failures
            }
        }

        public static void Uninstall()
        {
            Thread? thread;
            lock (Gate)
            {
                thread = _thread;
                _thread = null;
                _running = false;
                _onF9 = null;
            }
            thread?.Join(TimeSpan.FromSeconds(2));
        }

        private static void EventLoop(TaskCompletionSource<bool> ready)
        {
            var display = IntPtr.Zero;
            var keycode = 0;
            var root = IntPtr.Zero;
            try
            {
                display = XOpenDisplay(null);
                if (display == IntPtr.Zero)
                    throw new InvalidOperationException("Could not open the X display.");
                root = XDefaultRootWindow(display);
                keycode = XKeysymToKeycode(display, XK_F9);
                if (keycode == 0)
                    throw new InvalidOperationException("No keycode for F9 on this keyboard.");
                foreach (var mod in new[] { 0u, Mod2Mask })
                    XGrabKey(display, keycode, mod, root, true, GrabModeAsync, GrabModeAsync);
                XFlush(display);
                ready.TrySetResult(true);

                while (_running)
                {
                    while (XPending(display) > 0)
                    {
                        XNextEvent(display, out var ev);
                        if (ev.type == KeyPress) _onF9?.Invoke();
                    }
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
            finally
            {
                if (display != IntPtr.Zero)
                {
                    if (keycode != 0 && root != IntPtr.Zero)
                    {
                        foreach (var mod in new[] { 0u, Mod2Mask })
                            XUngrabKey(display, keycode, mod, root);
                        XFlush(display);
                    }
                    XCloseDisplay(display);
                }
                lock (Gate) { if (_thread?.Name == "F9 stop hotkey") _thread = null; }
            }
        }
    }
}
