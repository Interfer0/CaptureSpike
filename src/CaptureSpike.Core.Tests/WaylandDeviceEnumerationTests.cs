// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Capture;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for the Wayland branches of Linux device enumeration.
///
/// Screen and Window capture go through xdg-desktop-portal, and the
/// portal dialog is what lets the user pick what to share. There is no
/// reliable monitor list to enumerate on Wayland: xrandr is often absent,
/// and under XWayland it reports the XWayland root window rather than the
/// compositor's real outputs. These tests pin the behaviour that we do not
/// shell out to xrandr on Wayland and that the returned entry carries an
/// empty Id, which is what CaptureFactory's Wayland branch expects (it
/// ignores Source.Device and lets the portal decide).
/// </summary>
public sealed class WaylandDeviceEnumerationTests
{
    private static void SetWayland()
    {
        System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
        System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");
        System.Environment.SetEnvironmentVariable("DISPLAY", null);
    }

    private static void SetX11()
    {
        System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "x11");
        System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        System.Environment.SetEnvironmentVariable("DISPLAY", ":0");
    }

    [Fact]
    public void Screen_OnWayland_ReturnsSinglePortalEntry()
    {
        SetWayland();
        try
        {
            Assert.Equal(LinuxSession.DisplayServer.Wayland, LinuxSession.Current);

            var devices = DeviceEnumeration.GetDevices(SourceKind.Screen, "ffmpeg");

            var entry = Assert.Single(devices);
            // Empty Id: CaptureFactory's Wayland branch ignores
            // Source.Device, so a fabricated geometry would be a lie.
            Assert.Equal("", entry.Id);
            Assert.Contains("Share dialog", entry.Label);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        }
    }

    [Fact]
    public void Window_OnWayland_ReturnsSinglePortalEntry()
    {
        SetWayland();
        try
        {
            var devices = DeviceEnumeration.GetDevices(SourceKind.Window, "ffmpeg");

            var entry = Assert.Single(devices);
            Assert.Equal("", entry.Id);
            Assert.Contains("Share dialog", entry.Label);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        }
    }

    /// <summary>
    /// The regression this guards: on Wayland the old code shelled out to
    /// xrandr --listmonitors. Where xrandr is missing that threw out of
    /// GetMonitors, and where XWayland provides it the parsed result
    /// described the XWayland root window, not a real output. Either way
    /// the dropdown showed something wrong. On X11 we must keep using it,
    /// so that path is asserted to still be the xrandr one.
    /// </summary>
    [Fact]
    public void Screen_OnX11_StillEnumeratesMonitors()
    {
        SetX11();
        try
        {
            Assert.Equal(LinuxSession.DisplayServer.X11, LinuxSession.Current);

            // May legitimately be empty when xrandr is unavailable in the
            // test environment. What matters is that this path does not
            // return the Wayland placeholder, which would mean the branch
            // is miswired.
            var devices = DeviceEnumeration.GetDevices(SourceKind.Screen, "ffmpeg");

            Assert.DoesNotContain(devices, d => d.Label.Contains("Share dialog"));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            System.Environment.SetEnvironmentVariable("DISPLAY", null);
        }
    }
}