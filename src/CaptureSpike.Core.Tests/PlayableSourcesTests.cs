// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using CaptureSpike.Core.Playback;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for the playback source filter.
///
/// The regression: MpvPlaybackEngine checked File.Exists but not length,
/// so a 0-byte source reached mpv. mpv fails to demux an empty input and
/// takes the entire take down with it, so one dead source killed playback
/// of the sources that were fine. A 0-byte file is a real outcome on
/// Wayland, because a screencast is damage-driven and sharing a window
/// that never repaints yields a session that reaches STREAMING and then
/// never emits a buffer.
/// </summary>
public sealed class PlayableSourcesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cs-playable-" + Guid.NewGuid().ToString("N"));

    public PlayableSourcesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, int bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void MissingFile_IsNotUsable()
        => Assert.False(PlayableSources.IsUsable(Path.Combine(_dir, "nope.mkv")));

    [Fact]
    public void EmptyFile_IsNotUsable()
        => Assert.False(PlayableSources.IsUsable(Write("empty.mkv", 0)));

    [Fact]
    public void NonEmptyFile_IsUsable()
        => Assert.True(PlayableSources.IsUsable(Write("ok.mkv", 16)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankPath_IsNotUsable(string? path)
        => Assert.False(PlayableSources.IsUsable(path));

    /// <summary>
    /// A directory has a length on some platforms, so guard that we do not
    /// treat one as a playable media file.
    /// </summary>
    [Fact]
    public void Directory_IsNotUsable()
        => Assert.False(PlayableSources.IsUsable(_dir));
}