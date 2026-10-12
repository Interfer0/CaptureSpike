// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Capture.Wayland;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for the cs-pw-helper stderr header parser.
/// The helper writes a single line of the form
/// "CSWH W=... H=... FPS=..." to stderr when the
/// PipeWire stream is ready. The C# side parses it
/// to learn the negotiated screen-capture size.
/// </summary>
public sealed class PwStreamProtocolTests
{
    [Fact]
    public void TryParseHeaderLine_ValidLine_ReturnsHeader()
    {
        var header = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1920 H=1080 FPS=30.00");
        Assert.NotNull(header);
        Assert.Equal(1920, header!.Value.Width);
        Assert.Equal(1080, header.Value.Height);
        Assert.Equal(30, header.Value.FramerateNum);
        Assert.Equal(1, header.Value.FramerateDen);
        Assert.Equal(30.0, header.Value.FramerateFps);
    }

    [Fact]
    public void TryParseHeaderLine_Bgrx_DefaultsToBgra()
    {
        // The C helper's emit_header_line doesn't include
        // a format code; the C# side defaults to BGRA,
        // which is what PipeWire delivers on every
        // compositor we've seen. If we ever need to
        // disambiguate, we add a "F=" field to the
        // helper's line.
        var header = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1280 H=720 FPS=60.00");
        Assert.NotNull(header);
        Assert.Equal(PwStreamProtocol.PixelFormat.Bgra, header!.Value.Format);
    }

    [Fact]
    public void TryParseHeaderLine_59_94_UsesRational()
    {
        // 59.94 fps = 60000/1001. The double -> rational
        // conversion snaps to the closest known value so
        // we don't lose precision when forwarding to
        // ffmpeg's -framerate.
        var header = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1920 H=1080 FPS=59.94");
        Assert.NotNull(header);
        Assert.Equal(60000, header!.Value.FramerateNum);
        Assert.Equal(1001, header.Value.FramerateDen);
    }

    [Fact]
    public void TryParseHeaderLine_29_97_UsesRational()
    {
        var header = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1920 H=1080 FPS=29.97");
        Assert.NotNull(header);
        Assert.Equal(30000, header!.Value.FramerateNum);
        Assert.Equal(1001, header.Value.FramerateDen);
    }

    [Fact]
    public void TryParseHeaderLine_NotALine_ReturnsNull()
    {
        Assert.Null(PwStreamProtocol.TryParseHeaderLine(""));
        Assert.Null(PwStreamProtocol.TryParseHeaderLine("random text"));
        Assert.Null(PwStreamProtocol.TryParseHeaderLine("CSWH extra text"));
    }

    [Fact]
    public void TryParseHeaderLine_NotCSWH_ReturnsNull()
    {
        Assert.Null(PwStreamProtocol.TryParseHeaderLine(
            "FOO W=1920 H=1080 FPS=30.00"));
    }

    [Fact]
    public void TryParseHeaderLine_MissingFields_ReturnsNull()
    {
        Assert.Null(PwStreamProtocol.TryParseHeaderLine("CSWH W=1920 H=1080"));
        Assert.Null(PwStreamProtocol.TryParseHeaderLine("CSWH W=1920 FPS=30.00"));
        Assert.Null(PwStreamProtocol.TryParseHeaderLine("CSWH H=1080 FPS=30.00"));
    }

    [Fact]
    public void TryParseHeaderLine_InvalidNumbers_ReturnsNull()
    {
        Assert.Null(PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=abc H=1080 FPS=30.00"));
        Assert.Null(PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=0 H=1080 FPS=30.00"));
        Assert.Null(PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1920 H=-1 FPS=30.00"));
        Assert.Null(PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=1920 H=1080 FPS=0"));
    }

    [Fact]
    public void TryParseHeaderLine_WithLeadingTrailingWhitespace_Tolerant()
    {
        var header = PwStreamProtocol.TryParseHeaderLine(
            "  CSWH W=640 H=480 FPS=25.00  \n");
        Assert.NotNull(header);
        Assert.Equal(640, header!.Value.Width);
        Assert.Equal(480, header.Value.Height);
        Assert.Equal(25, header.Value.FramerateNum);
    }

    [Fact]
    public void TryParseHeaderLine_ExtraFields_StillParses()
    {
        // The C helper might add fields in the future
        // (e.g. "F=BGRA"). The parser should ignore
        // anything it doesn't recognize, as long as
        // the required W/H/FPS are present.
        var header = PwStreamProtocol.TryParseHeaderLine(
            "CSWH W=800 H=600 FPS=24.00 F=BGRA");
        Assert.NotNull(header);
        Assert.Equal(800, header!.Value.Width);
    }
}
