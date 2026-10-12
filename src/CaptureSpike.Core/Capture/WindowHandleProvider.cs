// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Runtime.InteropServices;

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Provides the HWND of the application's main window to
/// native code (specifically, the WGC picker on Windows
/// needs the HWND to make the picker dialog modal and to
/// display the picker at all in a desktop app).
///
/// The UI layer sets the value at startup. Core code
/// (notably <see cref="WindowsGraphicsCapturer"/>) reads
/// it when it needs to talk to a windowing system API.
///
/// On non-Windows or before the UI is up, the value is
/// null and the capturer falls back to no-op or throws
/// a clear error.
/// </summary>
public static class WindowHandleProvider
{
    private static nint? _mainWindowHandle;

    /// <summary>
    /// Set by the UI layer at app startup. The handle is
    /// the platform window handle (HWND on Windows, X11
    /// window ID on Linux, etc.). Set to null to clear.
    /// </summary>
    public static void SetMainWindowHandle(nint? handle) => _mainWindowHandle = handle;

    /// <summary>
    /// Read by Core code that needs a window handle. Returns
    /// null if the handle hasn't been set yet (e.g. the UI
    /// isn't initialized, or the user is on a platform that
    /// doesn't have the concept of a main window).
    /// </summary>
    public static nint? MainWindowHandle => _mainWindowHandle;
}

/// <summary>
/// Internal accessor for the ffmpeg binary path. Tests
/// can override this via SetOverride to inject a mock
/// ffmpeg without touching the real /usr/bin/ffmpeg.
/// </summary>
public static class FfmpegPathOverride
{
    private static string? _override;
    public static string? Override
    {
        get => _override;
        set => _override = value;
    }
}

/// <summary>
/// Runtime OS detection. Wrapped here so unit tests can
/// override the platform without mocking OperatingSystem
/// (which is a static class and hard to fake).
/// </summary>
public static class CurrentPlatform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
}
