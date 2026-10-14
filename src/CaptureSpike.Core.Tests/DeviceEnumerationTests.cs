// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Capture;
using Xunit;

public sealed class DeviceEnumerationTests
{
    [Fact]
    public void ParseDShowDevices_WithKindTags()
    {
        var output = """
            [dshow @ 1] "Integrated Camera" (video)
            [dshow @ 1] "Microphone Array" (audio)
            [dshow @ 1] "USB Camera" (video)
            """;
        Assert.Equal(["Integrated Camera", "USB Camera"],
            DeviceParsers.ParseDShowDevices(output, "(video)").ToList());
        Assert.Equal(["Microphone Array"],
            DeviceParsers.ParseDShowDevices(output, "(audio)").ToList());
    }

    [Fact]
    public void ParseXrandrMonitors_PrimaryFirst()
    {
        var output = """
            Monitors: 2
             0: +*HDMI-1 1920/510x1080/290+0+0  HDMI-1
             1: +DP-1 1920/510x1080/290+1920+0  DP-1
            """;
        var monitors = DeviceParsers.ParseXrandrMonitors(output);
        Assert.Equal(2, monitors.Count);
        Assert.True(monitors[0].Primary);
        Assert.Equal("HDMI-1", monitors[0].Name);
        Assert.Equal("1920x1080+0+0", monitors[0].ToDeviceId());
        Assert.False(monitors[1].Primary);
        Assert.Equal("1920x1080+1920+0", monitors[1].ToDeviceId());
        Assert.Contains("primary", monitors[0].ToLabel());
    }

    [Fact]
    public void TryParseGeometry_RoundTrips()
    {
        Assert.True(DeviceParsers.TryParseGeometry("1920x1080+1920+0", out var geo));
        Assert.Equal(1920, geo.Width);
        Assert.Equal(1080, geo.Height);
        Assert.Equal(1920, geo.X);
        Assert.Equal(0, geo.Y);
        Assert.False(DeviceParsers.TryParseGeometry(null, out _));
        Assert.False(DeviceParsers.TryParseGeometry("", out _));
        Assert.False(DeviceParsers.TryParseGeometry("desktop", out _));
        Assert.False(DeviceParsers.TryParseGeometry("not a window title", out _));
    }

    /// <summary>
    /// Regression test for the bug where
    /// <c>TryParseHwnd("hwnd:0x12b0dba")</c> returned
    /// false because <c>long.TryParse(..., NumberStyles.HexNumber)</c>
    /// doesn't accept the <c>0x</c> prefix as part of a
    /// hex literal. The picker then fell through to the
    /// system WGC picker, which the user does not want
    /// at record time.
    /// </summary>
    [Fact]
    public void TryParseHwnd_StripsPrefix()
    {
        // The exact format DeviceEnumeration.GetWindows
        // produces. Lowercase hex, with the 0x prefix.
        Assert.True(DeviceParsers.TryParseHwnd("hwnd:0x12b0dba", out var hwnd));
        Assert.Equal(0x12b0dba, hwnd.ToInt64());

        // Uppercase hex.
        Assert.True(DeviceParsers.TryParseHwnd("hwnd:0x12B0DBA", out var hwnd2));
        Assert.Equal(0x12B0DBA, hwnd2.ToInt64());

        // HWND-prefix variant (the "HWND:" form).
        Assert.True(DeviceParsers.TryParseHwnd("HWND:0xABCDEF", out var hwnd3));
        Assert.Equal(0xABCDEF, hwnd3.ToInt64());

        // 32-bit HWND (0) and full 64-bit.
        Assert.True(DeviceParsers.TryParseHwnd("hwnd:0x0", out var zero));
        Assert.Equal(IntPtr.Zero, zero);

        // Non-hwnd device ids must be rejected.
        Assert.False(DeviceParsers.TryParseHwnd("", out _));
        Assert.False(DeviceParsers.TryParseHwnd(null, out _));
        Assert.False(DeviceParsers.TryParseHwnd("1920x1080+0+0", out _));
        Assert.False(DeviceParsers.TryParseHwnd("Notepad", out _));
        Assert.False(DeviceParsers.TryParseHwnd("hwnd:not-hex", out _));
    }

    [Fact]
    public void ParsePactlSources_SkipsMonitors()
    {
        var output = """
            Source #1
            	Name: alsa_output.pci-0000_00_1f.3.analog-stereo.monitor
            	Description: Monitor of Built-in Audio Analog Stereo
            Source #2
            	Name: alsa_input.usb-046d_HD_Pro_Webcam_C920-02.analog-stereo
            	Description: HD Pro Webcam C920 Analog Stereo
            """;
        var sources = DeviceParsers.ParsePactlSources(output);
        var only = Assert.Single(sources);
        Assert.Equal("alsa_input.usb-046d_HD_Pro_Webcam_C920-02.analog-stereo", only.Id);
        Assert.Equal("HD Pro Webcam C920 Analog Stereo", only.Label);
    }

    [Fact]
    public void ParsePactlSinks_UsesMonitorIds()
    {
        var output = """
            Sink #0
            	Name: alsa_output.pci-0000_00_1f.3.analog-stereo
            	Description: Built-in Audio Analog Stereo
            """;
        var sinks = DeviceParsers.ParsePactlSinks(output);
        var only = Assert.Single(sinks);
        Assert.Equal("alsa_output.pci-0000_00_1f.3.analog-stereo.monitor", only.Id);
        Assert.Contains("Built-in Audio Analog Stereo", only.Label);
    }
}
