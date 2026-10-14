// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Capture;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for the Wayland device-id round trip.
///
/// The device picker persists the selected DeviceOption.Id into
/// Source.Device and matches it back when the list is rebuilt. That
/// round trip is where selections were being lost:
///
/// "Default" uses the empty id, and the setter stores an empty id as
/// null, so the raw comparison d.Id == Source.Device could never match
/// it. The Wayland placeholder originally shared that empty id, so its
/// selection was stored as null and then always came back as Default.
///
/// The placeholder now has its own id and must survive enumeration with
/// that id intact, and must be distinct from "Default" for the above
/// reason to hold.
/// </summary>
public sealed class WaylandDeviceIdTests
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

    private const string DefaultId = "";

    [Fact]
    public void PortalPlaceholder_IsNotTheDefaultId()
    {
        // If these ever collide again, a saved placeholder is stored as
        // null and reloads as Default, which is the original bug.
        Assert.NotEqual(DefaultId, DeviceEnumeration.WaylandPortalDeviceId);
        Assert.False(string.IsNullOrWhiteSpace(DeviceEnumeration.WaylandPortalDeviceId));
    }

    [Fact]
    public void PortalPlaceholder_SurvivesEnumerationWithItsId()
    {
        SetWayland();
        try
        {
            foreach (var kind in new[] { SourceKind.Screen, SourceKind.Window })
            {
                var devices = DeviceEnumeration.GetDevices(kind, "ffmpeg");
                var entry = Assert.Single(devices);
                Assert.Equal(DeviceEnumeration.WaylandPortalDeviceId, entry.Id);
                // Must never be mistaken for a missing device.
                Assert.DoesNotContain("not connected", entry.Label);
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        }
    }

    /// <summary>
    /// The persisted form is what the UI writes and reads back. The
    /// placeholder must round trip through it unchanged, which requires
    /// that the id is neither null nor empty.
    /// </summary>
    [Theory]
    [InlineData(SourceKind.Screen)]
    [InlineData(SourceKind.Window)]
    public void PortalPlaceholder_RoundTripsThroughPersistedForm(SourceKind kind)
    {
        SetWayland();
        try
        {
            var id = DeviceEnumeration.GetDevices(kind, "ffmpeg").Single().Id;

            // This mirrors the setter: empty is normalised to null.
            string? persisted = string.IsNullOrEmpty(id) ? null : id;
            Assert.NotNull(persisted);

            // ...and the load path: the persisted value is matched back
            // against the freshly enumerated ids.
            var reloaded = DeviceEnumeration.GetDevices(kind, "ffmpeg");
            var want = persisted ?? "";
            Assert.Contains(reloaded, d => d.Id == want);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            System.Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
        }
    }

    [Fact]
    public void X11ScreenIds_AreNotThePortalPlaceholder()
    {
        SetX11();
        try
        {
            var devices = DeviceEnumeration.GetDevices(SourceKind.Screen, "ffmpeg");
            Assert.DoesNotContain(devices,
                d => d.Id == DeviceEnumeration.WaylandPortalDeviceId);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            System.Environment.SetEnvironmentVariable("DISPLAY", null);
        }
    }
}