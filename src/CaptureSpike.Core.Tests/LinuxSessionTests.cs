// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Capture;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for LinuxSession display-server detection.
/// The CaptureFactory branches on this to fail fast on
/// Wayland (with a clear message) or fall through to
/// x11grab on X11. We test the detection directly; the
/// actual CaptureFactory integration is exercised in
/// the end-to-end / synthetic recorder tests.
/// </summary>
public sealed class LinuxSessionTests
{
    private static void ClearEnv()
    {
        // Save and restore in SetUp/TearDown would be
        // ideal, but .NET's IClassFixture-style cleanup
        // is overkill for this. Just unset the variables
        // we touch; tests run sequentially in xunit v3.
        System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
        System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        System.Environment.SetEnvironmentVariable("DISPLAY", null);
    }

    [Fact]
    public void XdgSessionType_X11_Returns_X11()
    {
        ClearEnv();
        System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "x11");
        try
        {
            Assert.Equal(LinuxSession.DisplayServer.X11, LinuxSession.Current);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void XdgSessionType_Wayland_Returns_Wayland()
    {
        ClearEnv();
        System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
        try
        {
            Assert.Equal(LinuxSession.DisplayServer.Wayland, LinuxSession.Current);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void XdgSessionType_TTY_Returns_Headless()
    {
        ClearEnv();
        System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "tty");
        try
        {
            Assert.Equal(LinuxSession.DisplayServer.Headless, LinuxSession.Current);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void NoEnvVars_Returns_Headless()
    {
        ClearEnv();
        try
        {
            // No XDG_SESSION_TYPE, no DISPLAY, no WAYLAND_DISPLAY.
            // This is the CI / TTY / SSH-without-X-forwarding case.
            Assert.Equal(LinuxSession.DisplayServer.Headless, LinuxSession.Current);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void XWayland_FallsBackTo_X11()
    {
        // Both WAYLAND_DISPLAY and DISPLAY set: XWayland is
        // forwarding X11 clients through Wayland. x11grab
        // still works because XWayland provides an X server.
        // We want the detection to return X11 (not Wayland)
        // so the existing x11grab path is taken.
        ClearEnv();
        System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");
        System.Environment.SetEnvironmentVariable("DISPLAY", ":0");
        try
        {
            Assert.Equal(LinuxSession.DisplayServer.X11, LinuxSession.Current);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void WaylandOnly_Returns_Wayland()
    {
        ClearEnv();
        System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", "wayland-0");
        // DISPLAY unset: pure Wayland, no XWayland.
        try
        {
            Assert.Equal(LinuxSession.DisplayServer.Wayland, LinuxSession.Current);
        }
        finally { ClearEnv(); }
    }

    [Fact]
    public void CurrentName_Formats_Correctly()
    {
        // Quick sanity check on the human-readable name.
        // (Doesn't set env vars; just checks the strings
        // are non-empty and not the placeholder.)
        Assert.NotEmpty(LinuxSession.CurrentName);
        Assert.DoesNotContain("?", LinuxSession.CurrentName);
    }
}
