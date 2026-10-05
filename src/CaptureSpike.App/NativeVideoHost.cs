using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace CaptureSpike.App;

/// <summary>
/// A <see cref="NativeControlHost"/> that owns a plain native child window for
/// mpv's <c>--wid</c> embedding. Windows uses a STATIC control; Linux/X11 uses
/// a simple X11 window. If creation fails the caller should fall back to
/// letting mpv open its own window.
/// </summary>
public sealed class MpvVideoHost : NativeControlHost
{
    /// <summary>Raised with the native child handle once created.</summary>
    public event Action<IntPtr>? HandleCreated;
    /// <summary>Raised when the native child handle is destroyed.</summary>
    public event Action? HandleDestroyed;

    private IntPtr _child;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _child = NativeVideoInterop.CreateChild(parent.Handle,
            (int)Bounds.Width, (int)Bounds.Height);
        HandleCreated?.Invoke(_child);
        return new PlatformHandle(_child, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        HandleDestroyed?.Invoke();
        if (_child != IntPtr.Zero)
        {
            NativeVideoInterop.DestroyChild(_child);
            _child = IntPtr.Zero;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_child != IntPtr.Zero)
            NativeVideoInterop.ResizeChild(_child, (int)Bounds.Width, (int)Bounds.Height);
    }
}

internal static class NativeVideoInterop
{
    public static IntPtr CreateChild(IntPtr parent, int w, int h)
    {
        if (OperatingSystem.IsWindows()) return Win32Child.Create(parent, w, h);
        if (OperatingSystem.IsLinux()) return X11Child.Create(parent, w, h);
        throw new PlatformNotSupportedException("Video embedding is supported on Windows and Linux/X11.");
    }

    public static void DestroyChild(IntPtr child)
    {
        if (OperatingSystem.IsWindows()) Win32Child.Destroy(child);
        else if (OperatingSystem.IsLinux()) X11Child.Destroy(child);
    }

    public static void ResizeChild(IntPtr child, int w, int h)
    {
        if (OperatingSystem.IsWindows()) Win32Child.Resize(child, w, h);
        else if (OperatingSystem.IsLinux()) X11Child.Resize(child, w, h);
    }

    private static class Win32Child
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
            int style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        public static IntPtr Create(IntPtr parent, int w, int h)
        {
            var scale = GetDpiForWindow(parent) / 96.0;
            var hwnd = CreateWindowEx(0, "STATIC", string.Empty,
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)),
                parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the video child window.");
            return hwnd;
        }

        public static void Destroy(IntPtr hwnd) => DestroyWindow(hwnd);

        public static void Resize(IntPtr hwnd, int w, int h)
        {
            var parent = GetParent(hwnd);
            var scale = parent != IntPtr.Zero ? GetDpiForWindow(parent) / 96.0 : 1.0;
            MoveWindow(hwnd, 0, 0, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), true);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hwnd);
    }

    private static class X11Child
    {
        [DllImport("libX11")]
        private static extern IntPtr XOpenDisplay(string? display);
        [DllImport("libX11")]
        private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11")]
        private static extern int XDefaultScreen(IntPtr display);
        [DllImport("libX11")]
        private static extern ulong XBlackPixel(IntPtr display, int screen);
        [DllImport("libX11")]
        private static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent,
            int x, int y, uint width, uint height, uint borderWidth, ulong border, ulong background);
        [DllImport("libX11")]
        private static extern int XMapWindow(IntPtr display, IntPtr window);
        [DllImport("libX11")]
        private static extern int XDestroyWindow(IntPtr display, IntPtr window);
        [DllImport("libX11")]
        private static extern int XMoveResizeWindow(IntPtr display, IntPtr window,
            int x, int y, uint width, uint height);
        [DllImport("libX11")]
        private static extern int XFlush(IntPtr display);

        private static readonly Dictionary<IntPtr, IntPtr> Displays = new();

        public static IntPtr Create(IntPtr parent, int w, int h)
        {
            var display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
                throw new InvalidOperationException("Could not open the X display.");
            try
            {
                var screen = XDefaultScreen(display);
                var black = XBlackPixel(display, screen);
                var win = XCreateSimpleWindow(display, parent, 0, 0,
                    (uint)Math.Max(1, w), (uint)Math.Max(1, h), 0, black, black);
                if (win == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the X11 child window.");
                XMapWindow(display, win);
                XFlush(display);
                lock (Displays) Displays[win] = display;
                return win;
            }
            catch
            {
                XCloseDisplay(display);
                throw;
            }
        }

        public static void Destroy(IntPtr win)
        {
            IntPtr display;
            lock (Displays)
            {
                if (!Displays.Remove(win, out display)) return;
            }
            XDestroyWindow(display, win);
            XFlush(display);
            XCloseDisplay(display);
        }

        public static void Resize(IntPtr win, int w, int h)
        {
            IntPtr display;
            lock (Displays)
            {
                if (!Displays.TryGetValue(win, out display)) return;
            }
            XMoveResizeWindow(display, win, 0, 0, (uint)Math.Max(1, w), (uint)Math.Max(1, h));
            XFlush(display);
        }
    }
}
