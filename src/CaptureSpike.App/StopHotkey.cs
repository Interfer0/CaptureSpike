using System.Runtime.InteropServices;

namespace CaptureSpike.App;

/// <summary>
/// Global F9 hotkey that stops the recording while the app window is minimized.
/// Windows uses a low-level keyboard hook (no window handle needed);
/// Linux/X11 grabs F9 on the root window from a background event thread.
/// </summary>
public static class StopHotkey
{
    public static void Install(Action onF9)
    {
        if (OperatingSystem.IsWindows()) WinHook.Install(onF9);
        else if (OperatingSystem.IsLinux()) X11Grab.Install(onF9);
        else throw new PlatformNotSupportedException(
            "The global stop hotkey is supported on Windows and Linux/X11.");
    }

    public static void Uninstall()
    {
        if (OperatingSystem.IsWindows()) WinHook.Uninstall();
        else if (OperatingSystem.IsLinux()) X11Grab.Uninstall();
    }

    // ----- Windows: WH_KEYBOARD_LL -----

    private static class WinHook
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_F9 = 0x79;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private static IntPtr _hook;
        private static HookProc? _proc; // kept alive while installed
        private static Action? _onF9;

        public static void Install(Action onF9)
        {
            if (_hook != IntPtr.Zero) throw new InvalidOperationException(
                "The stop hotkey is already installed.");
            _onF9 = onF9;
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                _proc = null;
                _onF9 = null;
                throw new InvalidOperationException(
                    "Could not install the F9 stop hotkey.");
            }
        }

        public static void Uninstall()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _proc = null;
            _onF9 = null;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN &&
                Marshal.ReadInt32(lParam) == VK_F9)
            {
                // Keep this fast: the VM marshals the real work to the UI thread.
                _onF9?.Invoke();
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
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
