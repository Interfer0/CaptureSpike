// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Globalization;

namespace CaptureSpike.Core.Capture.Wayland;

/// <summary>
/// Parses the single header line that cs-pw-helper writes
/// to stderr when the PipeWire stream is ready, and
/// holds the negotiated screen-capture dimensions.
/// See installer/linux/cs-pw-helper/cs-pw-helper.c for
/// the producer side.
///
/// Wire format on stderr (one line):
///   "CSWH W={width} H={height} FPS={fps}\n"
///
/// On the Unix socket itself, there is NO header — the
/// helper writes a flat stream of raw BGRA/BGR0 frames
/// that ffmpeg's rawvideo demuxer reads based on the
/// -video_size and -framerate args the .NET layer
/// passed to ffmpeg. This simplifies the protocol: ffmpeg
/// is a vanilla rawvideo consumer and we don't need to
/// intercept the stream to read a header.
/// </summary>
public static class PwStreamProtocol
{
    /// <summary>
    /// Pixel format codes from the wire protocol.
    /// Must match the C helper's g_frame_format values.
    /// </summary>
    public enum PixelFormat : uint
    {
        Bgra = 1,
        Bgr0 = 2,
    }

    /// <summary>
    /// Parsed screen-capture dimensions reported by the
    /// helper on stderr.
    /// </summary>
    public readonly record struct StreamHeader(
        int Width,
        int Height,
        PixelFormat Format,
        int FramerateNum,
        int FramerateDen)
    {
        /// <summary>
        /// Effective framerate as a double. Useful for
        /// logging; the framerate for ffmpeg is
        /// reconstructed from Num/Den to avoid
        /// rounding error.
        /// </summary>
        public double FramerateFps =>
            FramerateDen == 0 ? 0.0 : (double)FramerateNum / FramerateDen;
    }

    /// <summary>
    /// Parse a header line of the form
    /// "CSWH W=1920 H=1080 FPS=30.00" (or any double
    /// precision). Returns null if the line doesn't
    /// match the expected format.
    /// </summary>
    public static StreamHeader? TryParseHeaderLine(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("CSWH ", StringComparison.Ordinal)) return null;
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;

        int w = 0, h = 0;
        double fps = 0;
        for (int i = 1; i < parts.Length; i++)
        {
            var kv = parts[i].Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "W" when int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pw): w = pw; break;
                case "H" when int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ph): h = ph; break;
                case "FPS" when double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pf): fps = pf; break;
            }
        }
        if (w <= 0 || h <= 0 || fps <= 0) return null;

        var (num, den) = FpsToRational(fps);
        return new StreamHeader(w, h, PixelFormat.Bgra, num, den);
    }

    /// <summary>Convert a double fps back to a num/den
    /// rational. The protocol uses num/den so we don't
    /// lose precision on 59.94 (60000/1001).</summary>
    private static (int Num, int Den) FpsToRational(double fps)
    {
        var known = new (double V, int N, int D)[]
        {
            (30.0, 30, 1), (60.0, 60, 1), (25.0, 25, 1),
            (24.0, 24, 1), (23.976, 24000, 1001),
            (29.97, 30000, 1001), (59.94, 60000, 1001),
        };
        foreach (var (v, n, d) in known)
            if (Math.Abs(fps - v) < 0.01) return (n, d);
        return ((int)Math.Round(fps), 1);
    }
}
