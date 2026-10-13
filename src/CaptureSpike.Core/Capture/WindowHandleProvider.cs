// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

namespace CaptureSpike.Core.Capture;

// WindowHandleProvider was removed when the WGC system
// picker was removed (Daniel's feedback: 'the picker
// did not start, nor should it be needed'). The capturer
// uses the HWND stored in the source's device field
// directly via IGraphicsCaptureItemInterop.
//
// If a future API needs the main window's HWND (e.g.
// for COM init that requires a parent), reintroduce
// this class and wire App.axaml.cs to call Set.

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
