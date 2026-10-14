// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Capture.Wayland;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for the WxH+X+Y device string parser and the ffmpeg crop
/// filter it produces. The same string is consumed by x11grab on X11
/// and by a -vf crop= on Wayland, so a project that was authored on
/// either platform keeps its region on the other.
/// </summary>
public sealed class CropRegionTests
{
    [Theory]
    [InlineData("1920x1080+0+0", 1920, 1080, 0, 0)]
    [InlineData("1280x720+100+50", 1280, 720, 100, 50)]
    [InlineData("640X480+1+2", 640, 480, 1, 2)]  // capital X tolerated
    [InlineData("  800x600+0+0  ", 800, 600, 0, 0)]  // surrounding space
    public void Parse_ValidString_ReturnsRegion(
        string device, int w, int h, int x, int y)
    {
        var region = CropRegion.Parse(device);
        Assert.NotNull(region);
        Assert.Equal(new CropRegion(w, h, x, y), region.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1920x1080")]              // no +X+Y
    [InlineData("1920x1080+0")]            // one +
    [InlineData("1920x1080+0+0+0")]        // extra +
    [InlineData("0x0+0+0")]                // zero size rejected
    [InlineData("-1x1080+0+0")]           // negative size rejected (parse guard)
    [InlineData("not-a-geometry")]
    [InlineData("1080x0+0+0")]
    public void Parse_Garbage_ReturnsNull(string? device)
    {
        Assert.Null(CropRegion.Parse(device));
    }

    [Fact]
    public void ToFfmpegArgs_ProducesCropFilter()
    {
        var r = new CropRegion(1920, 1080, 0, 0);
        Assert.Equal(new[] { "-vf", "crop=1920:1080:0:0" }, r.ToFfmpegArgs());
    }

    [Fact]
    public void ToFfmpegArgs_EmptyRegion_EmitsNothing()
    {
        // A zero-size region means "no crop requested" - the ffmpeg
        // args must not be polluted with a -vf crop=0:0:0:0 that would
        // black out the output.
        Assert.Empty(new CropRegion(0, 0, 0, 0).ToFfmpegArgs());
    }
}