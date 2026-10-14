// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Capture.Wayland;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for the FMT field of the CSWH wire protocol.
///
/// The helper used to send only W, H and FPS, so the .NET side had
/// nothing to go on and hardcoded BGRA for every source. That made two
/// different things indistinguishable: Screen capture negotiates BGRx
/// and Window capture negotiates BGRA. Decoded output is identical, but
/// the consumer was told the wrong thing, and the Bgr0 branch in
/// BuildFfmpegArgs was unreachable.
///
/// FMT is optional so a helper built before it existed still parses.
/// </summary>
public sealed class PwStreamFormatTests
{
    [Theory]
    [InlineData("1", PwStreamProtocol.PixelFormat.Bgra)]
    [InlineData("2", PwStreamProtocol.PixelFormat.Bgr0)]
    [InlineData("3", PwStreamProtocol.PixelFormat.Bgrx)]
    [InlineData("4", PwStreamProtocol.PixelFormat.Rgbx)]
    public void FmtField_IsParsed(string code, PwStreamProtocol.PixelFormat expected)
    {
        var header = PwStreamProtocol.TryParseHeaderLine(
            $"CSWH W=1920 H=1080 FPS=30.00 FMT={code}");
        Assert.NotNull(header);
        Assert.Equal(expected, header!.Value.Format);
    }

    [Fact]
    public void FmtField_DistinguishesScreenFromWindow()
    {
        // What the helper actually emits today. Screen came back as
        // BGRx (spa format 8) and Window as BGRA (spa format 12).
        var screen = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1920 H=1080 FPS=30.00 FMT=3");
        var window = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1280 H=720 FPS=30.00 FMT=1");

        Assert.Equal(PwStreamProtocol.PixelFormat.Bgrx, screen!.Value.Format);
        Assert.Equal(PwStreamProtocol.PixelFormat.Bgra, window!.Value.Format);
        Assert.NotEqual(screen.Value.Format, window.Value.Format);
    }

    [Fact]
    public void FmtField_Absent_StillParses()
    {
        // Back-compat with a helper built before FMT existed.
        var header = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1920 H=1080 FPS=30.00");
        Assert.NotNull(header);
        Assert.Equal(PwStreamProtocol.PixelFormat.Bgra, header!.Value.Format);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("abc")]
    [InlineData("")]
    public void FmtField_UnknownCode_FallsBackRatherThanThrowing(string code)
    {
        var header = PwStreamProtocol.TryParseHeaderLine(
            $"CSWH W=1920 H=1080 FPS=30.00 FMT={code}");
        Assert.NotNull(header);
        Assert.Equal(PwStreamProtocol.PixelFormat.Bgra, header!.Value.Format);
    }

    [Fact]
    public void FmtField_DoesNotDisturbTheOtherFields()
    {
        var header = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1280 H=720 FPS=59.94 FMT=3");
        Assert.NotNull(header);
        Assert.Equal(1280, header!.Value.Width);
        Assert.Equal(720, header.Value.Height);
        Assert.Equal(60000, header.Value.FramerateNum);
        Assert.Equal(1001, header.Value.FramerateDen);
    }

    [Theory]
    [InlineData("bgra", PwStreamProtocol.PixelFormat.Bgra)]
    [InlineData("bgr0", PwStreamProtocol.PixelFormat.Bgr0)]
    [InlineData("bgr0", PwStreamProtocol.PixelFormat.Bgrx)]
    // Rgbx is R,G,B,X - opposite channel order from Bgrx. ffmpeg's
    // rgb0 is the correct 32-bit packed label; previously this mapped
    // to bgr0 and produced a recording with R and B channels swapped.
    [InlineData("rgb0", PwStreamProtocol.PixelFormat.Rgbx)]
    public void FfmpegPixFmt_IsDerivedFromTheReportedFormat(
        string expectedArg, PwStreamProtocol.PixelFormat format)
    {
        Assert.Equal(expectedArg, WaylandCapturer.PixelFormatToFfmpegArg(format));
    }
}