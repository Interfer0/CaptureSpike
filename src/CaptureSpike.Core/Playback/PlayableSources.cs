// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

namespace CaptureSpike.Core.Playback;

/// <summary>
/// Decides whether a recorded source file can be handed to mpv as an
/// input.
///
/// The existence check alone is not enough. A screencast source that
/// never produced a frame leaves a 0-byte file behind, and that happens
/// legitimately: a Wayland screencast is damage-driven, so sharing a
/// window that never repaints yields a session that reaches STREAMING
/// and then never emits a buffer. ffmpeg then writes an empty container,
/// and mpv refuses to load it ("could not load a media input"), which
/// takes down playback of every other source in the take with it.
///
/// So playback must filter on size, not just presence.
/// </summary>
public static class PlayableSources
{
    /// <summary>
    /// True when <paramref name="path"/> names a file that exists and has
    /// at least one byte. Anything unreadable is treated as unusable:
    /// a source that cannot be opened will not help the take.
    /// </summary>
    public static bool IsUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            // Permissions, races with the recorder still flushing, a
            // path that is not valid on this filesystem. Either way we
            // cannot play it.
            return false;
        }
    }
}