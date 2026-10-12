// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Identifies the current Linux display server (X11, Wayland,
/// or unknown). Used by the capture layer to pick the right
/// backend. On Wayland, screen capture is not yet supported
/// (the design lives in docs/WAYLAND.md); the capture path
/// raises a clear error rather than failing with a cryptic
/// ffmpeg message.
/// </summary>
public static class LinuxSession
{
    public enum DisplayServer
    {
        Unknown,
        X11,
        Wayland,
        /// <summary>
        /// Headless / no display. TTY, SSH without X forwarding,
        /// CI runners, etc. Capture cannot work here.
        /// </summary>
        Headless,
    }

    /// <summary>
    /// The display server the current process is running on.
    /// X11 and Wayland are detected via the standard
    /// environment variables; if both are unset, we assume
    /// headless (a TTY or container without a display).
    /// </summary>
    public static DisplayServer Current
    {
        get
        {
            // XDG_SESSION_TYPE is the modern, reliable signal.
            // It's set by virtually every display manager
            // (gdm, sddm, lightdm) and by the Wayland session
            // wrapper. Falls back to env-var detection if
            // not set (some container environments omit it).
            var sessionType = System.Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            if (!string.IsNullOrEmpty(sessionType))
            {
                return sessionType switch
                {
                    "x11" => DisplayServer.X11,
                    "wayland" => DisplayServer.Wayland,
                    "tty" => DisplayServer.Headless,
                    _ => DisplayServer.Unknown,
                };
            }

            // Fallback: WAYLAND_DISPLAY is set by the Wayland
            // compositor, DISPLAY is set by the X11 server.
            // Both being set means XWayland is forwarding
            // X11 clients through Wayland; in that case
            // X11 is the right answer (x11grab works because
            // XWayland provides an X server).
            var hasWayland = !string.IsNullOrEmpty(
                System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            var hasX11 = !string.IsNullOrEmpty(
                System.Environment.GetEnvironmentVariable("DISPLAY"));

            if (hasWayland && !hasX11) return DisplayServer.Wayland;
            if (hasX11) return DisplayServer.X11;
            return DisplayServer.Headless;
        }
    }

    /// <summary>
    /// Short human-readable name for the current display server
    /// ("X11", "Wayland", "headless", or "unknown"). Used in
    /// error messages and the Settings dialog.
    /// </summary>
    public static string CurrentName => Current switch
    {
        DisplayServer.X11 => "X11",
        DisplayServer.Wayland => "Wayland",
        DisplayServer.Headless => "headless (no display)",
        _ => "unknown",
    };
}
