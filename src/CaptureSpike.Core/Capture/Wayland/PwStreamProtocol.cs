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
///   "CSWH W={width} H={height} FPS={fps} FMT={format}\n"
///
/// FMT is optional so a helper built before it existed still parses.
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
    /// Must match the C helper's protocol_format_code().
    /// </summary>
    public enum PixelFormat : uint
    {
        Bgra = 1,
        Bgr0 = 2,
        /// <summary>
        /// BGR with an undefined fourth byte. Screen capture negotiates
        /// this one, Window capture negotiates <see cref="Bgra"/>. Both
        /// are four bytes per pixel in the same byte order, so the decoded
        /// output is identical; only the label differed and it was being
        /// reported as Bgra for both.
        /// </summary>
        Bgrx = 3,
        /// <summary>
        /// RGB with an undefined fourth byte. Byte order is R,G,R,X - the
        /// opposite channel order from <see cref="Bgrx"/>. Only test-mode
        /// sources (and certain v4l2 cameras) negotiate this; the previous
        /// code mapped it to Bgrx and the recorded file had its R and B
        /// channels swapped. FFMpeg's <c>rgb0</c> input format is the
        /// correct label for this layout.
        /// </summary>
        Rgbx = 4,
        /// <summary>
        /// RGBA: real alpha (not 0xFF padding like BGRx). Same byte layout
        /// as Bgra in memory, but a different name. ffmpeg accepts
        /// <c>bgra</c> for both, so the mapping reuses Bgra. Reserved here
        /// for future ffmpeg-side distinction (some encoders care about
        /// the alpha).
        /// </summary>
        Rgba = 5,
        /// <summary>
        /// YUYV 4:2:2 packed. ffmpeg's <c>yuyv422</c>. Rare on screencast
        /// sources (cameras use it, the portal typically does not) but
        /// listed in the EnumFormat so the source can pick it.
        /// </summary>
        Yuyv = 6,
        /// <summary>
        /// YUV 4:2:0 planar. ffmpeg's <c>yuv420p</c>. Same caveat as Yuyv.
        /// </summary>
        I420 = 7,
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
        var fmt = (PixelFormat?)null;
        for (int i = 1; i < parts.Length; i++)
        {
            var kv = parts[i].Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "W" when int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pw): w = pw; break;
                case "H" when int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ph): h = ph; break;
                case "FPS" when double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pf): fps = pf; break;
                case "FMT":
                    if (uint.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pu)
                        && Enum.IsDefined(typeof(PixelFormat), pu))
                    {
                        fmt = (PixelFormat)pu;
                    }
                    // FMT present but unparseable or unknown. Log and
                    // fall through to the default rather than silently
                    // dropping the hint - that hid a real helper-vs-
                    // parser drift in the past.
                    else
                    {
                        AppLog.Current?.WriteLine(
                            $"[cs-pw-helper] CSWH line has unknown FMT={kv[1]}; " +
                            "falling back to Bgra. This is a protocol drift " +
                            "between the helper and this .NET layer.");
                    }
                    break;
            }
        }
        if (w <= 0 || h <= 0 || fps <= 0) return null;

        var (num, den) = FpsToRational(fps);
        // FMT is optional. A helper built before the field existed does
        // not send it, and such a helper only ever emitted BGRx for
        // Screen, so Bgra is the safer of the two guesses here: the
        // decoder output is identical either way.
        return new StreamHeader(w, h, fmt ?? PixelFormat.Bgra, num, den);
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
