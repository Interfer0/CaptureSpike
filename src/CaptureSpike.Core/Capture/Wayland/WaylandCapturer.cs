// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace CaptureSpike.Core.Capture.Wayland;

/// <summary>
/// Records the screen on a Wayland session by composing
/// two subprocesses:
///
///   1. <c>cs-pw-helper</c> — talks to xdg-desktop-portal,
///      gets a PipeWire node ID, and writes raw BGRA
///      frames to a Unix socket. It also writes the
///      negotiated width/height/framerate to stderr as a
///      single line ("CSWH W=... H=... FPS=...") so the
///      C# layer can read it without touching the
///      socket (which ffmpeg owns).
///   2. <c>ffmpeg</c> — reads the socket as a rawvideo
///      input, encodes with libx264, writes the .mp4.
///
/// The wire protocol is intentionally simple: ffmpeg
/// reads a flat rawvideo stream with the format
/// pre-configured via command-line args
/// (<c>-pix_fmt bgra -video_size WxH -framerate N</c>).
/// No header on the socket means ffmpeg's rawvideo
/// demuxer works out of the box.
///
/// First-data detection comes from the inner
/// <see cref="FfmpegCapturer"/>'s progress parsing
/// (frame=N&gt;0 on stdout). We don't reimplement it.
/// </summary>

/// <summary>
/// A rectangular crop applied to a captured stream, expressed in the
/// same coordinate space as the stream itself. The Wayland portal
/// decides what the stream covers; this lets a project that was
/// authored on X11 (where <c>Source.Device</c> was a
/// <c>WxH+X+Y</c> geometry handed to ffmpeg) still produce a region
/// capture when run on Wayland, by cropping the negotiated stream
/// after the fact instead of asking the portal for it.
/// </summary>
public readonly record struct CropRegion(int Width, int Height, int X, int Y)
{
    /// <summary>
    /// Parses a <c>WxH+X+Y</c> device string into a crop, or null if
    /// the string is empty or malformed. Used for both the X11
    /// <c>x11grab</c> device and the Wayland crop, so a project
    /// configured on either platform keeps its region on the other.
    /// </summary>
    public static CropRegion? Parse(string? device)
    {
        if (string.IsNullOrWhiteSpace(device)) return null;
        var s = device.Trim();
        var x = s.IndexOf('+');
        if (x < 0) return null;
        var x2 = s.IndexOf('+', x + 1);
        if (x2 < 0) return null;
        var sizePart = s.AsSpan(0, x).ToString();
        var xPart = s.AsSpan(x + 1, x2 - x - 1).ToString();
        var yPart = s.AsSpan(x2 + 1).ToString();
        var wh = sizePart.Split('x', 'X');
        if (wh.Length != 2) return null;
        if (!int.TryParse(wh[0], out var w) || w <= 0) return null;
        if (!int.TryParse(wh[1], out var h) || h <= 0) return null;
        if (!int.TryParse(xPart, out var cx) || cx < 0) return null;
        if (!int.TryParse(yPart, out var cy) || cy < 0) return null;
        return new CropRegion(w, h, cx, cy);
    }

    /// <summary>
    /// ffmpeg <c>-vf crop=W:H:X:Y</c> arguments for this region, or
    /// empty when no crop is needed.
    /// </summary>
    public string[] ToFfmpegArgs() =>
        Width > 0 && Height > 0
            ? new[] { "-vf", $"crop={Width}:{Height}:{X}:{Y}" }
            : Array.Empty<string>();
}
public sealed class WaylandCapturer : ISourceCapturer
{
    private readonly string _helperPath;
    private readonly string _socketPath;
    private readonly int _framerate;
    private readonly int _sourceType;
    private readonly bool _includeCursor;
    private readonly CropRegion? _region;
    private readonly FfmpegCapturer _inner;
    private Process? _helper;
    private readonly StringBuilder _helperStderr = new();
    private readonly TaskCompletionSource<PwStreamProtocol.StreamHeader> _headerTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    // Set after the helper is spawned and the CSWH wait is in flight.
    // StartAsync is single-use: the header TCS, the stderr reader
    // task, and the helper process are all per-instance, and a second
    // call races against the first on every one of them. Throwing here
    // is louder and earlier than letting that race surface as a hang
    // or a duplicate Exited.
    private bool _started;

    public string SourceId { get; }
    public SourceKind Kind { get; }
    public string OutputFile { get; }
    public DateTimeOffset? FirstDataAt => _inner.FirstDataAt;

    /// <summary>
    /// Build a Wayland screen capturer.
    /// </summary>
    /// <param name="sourceId">Logical id of the palette source.</param>
    /// <param name="outputFile">Where the encoded .mp4 should land.</param>
    /// <param name="helperPath">Absolute path to the cs-pw-helper binary.
    /// The .NET layer resolves this from the bundled tools dir at
    /// publish time.</param>
    /// <param name="ffmpeg">Absolute path to the ffmpeg binary.</param>
    /// <param name="framerate">Frames per second to record at.</param>
    /// <param name="sourceType">1 = monitor, 2 = window, 4 = region. Maps
    /// to libportal's XdpScreenCastSourceType.</param>
    /// <param name="includeCursor">Whether the captured stream should
    /// include the mouse cursor.</param>
    public WaylandCapturer(
        string sourceId,
        string outputFile,
        string helperPath,
        string ffmpeg,
        int framerate,
        int sourceType = 1,
        bool includeCursor = false,
        CropRegion? crop = null)
    {
        SourceId = sourceId;
        _region = crop;
        Kind = SourceKind.Screen;
        OutputFile = outputFile;
        _helperPath = helperPath;
        _framerate = framerate;
        _sourceType = sourceType;
        _includeCursor = includeCursor;
        _socketPath = PickSocketPath(sourceId);

        // We construct the inner FfmpegCapturer lazily
        // because we don't know the negotiated
        // width/height until the helper reports it on
        // stderr. The ffmpeg args are set just before
        // delegating to StartAsync.
        _inner = new FfmpegCapturer(
            sourceId, SourceKind.Screen, outputFile, ffmpeg,
            arguments: Array.Empty<string>());
    }

    /// <summary>
    /// Pick a unique Unix socket path. Includes the
    /// process id, a per-take counter, and the source id so
    /// two takes at the same time (or back-to-back with the
    /// same source) don't collide. We use a counter rather
    /// than deleting a possibly-in-use stale socket.
    /// </summary>
    private static int s_takeCounter;
    private static string PickSocketPath(string sourceId)
    {
        var safeId = new string(sourceId
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        int n = Interlocked.Increment(ref s_takeCounter);
        // No File.Delete: a stale socket from another running
        // instance is none of our business. If we get EADDRINUSE
        // we should fail loudly rather than stomp someone.
        return $"/tmp/capturespike-pw-{Environment.ProcessId}-{n}-{safeId}.sock";
    }

    public async Task StartAsync(DateTimeOffset t0, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException(
                $"WaylandCapturer {SourceId} has already been started.");
        _started = true;

        // Spawn the helper. It opens the Unix socket and
        // listens; the portal dialog appears async (we
        // can't wait for the user's click in this method).
        var helperPsi = new ProcessStartInfo
        {
            FileName = _helperPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        helperPsi.ArgumentList.Add("--socket");
        helperPsi.ArgumentList.Add(_socketPath);
        helperPsi.ArgumentList.Add("--source-type");
        helperPsi.ArgumentList.Add(_sourceType.ToString());
        helperPsi.ArgumentList.Add("--framerate");
        helperPsi.ArgumentList.Add(_framerate.ToString());
        if (_includeCursor) helperPsi.ArgumentList.Add("--cursor");

        _helper = Process.Start(helperPsi)
            ?? throw new CaptureException(SourceId,
                $"Could not start the Wayland screen-capture helper at '{_helperPath}'.");
        _helper.EnableRaisingEvents = true;
        // If the helper dies before writing the CSWH line,
        // fail the wait fast instead of waiting the full
        // 60s timeout. The Exited event fires on a ThreadPool
        // thread; we capture the exit code and use it in
        // the error message below.
        _helper.Exited += (_, _) =>
        {
            int code = -1;
            try { code = _helper?.ExitCode ?? -1; } catch { }
            _headerTcs.TrySetException(new CaptureException(SourceId,
                $"cs-pw-helper exited with code {code} before writing the CSWH header. " +
                $"Stderr: {HelperStderrTail()}"));
        };
        _ = Task.Run(() => ReadHelperStderrAsync(_helper.StandardError, ct), ct);

        // Wait for the helper to print the negotiated
        // header line on stderr ("CSWH W=... H=... FPS=...").
        // This is the signal that the user has clicked
        // 'Share' in the portal dialog and the PipeWire
        // stream is ready. We give the user 60s to click.
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(TimeSpan.FromSeconds(60));
        PwStreamProtocol.StreamHeader header;
        try
        {
            header = await _headerTcs.Task.WaitAsync(headerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var helperExit = _helper?.HasExited == true ? (int?)_helper!.ExitCode : null;
            var msg = helperExit is not null
                ? $"cs-pw-helper exited with code {helperExit} before the screen capture " +
                  $"session started. Stderr: {HelperStderrTail()}"
                : "Timed out waiting for cs-pw-helper to start the screen capture " +
                  $"session. Did the portal dialog appear? Stderr: {HelperStderrTail()}";
            throw new CaptureException(SourceId, msg);
        }

        var ffmpegArgs = BuildFfmpegArgs(header);
        _inner.SetArguments(ffmpegArgs);

        await _inner.StartAsync(t0, ct).ConfigureAwait(false);
    }

    /// <summary>Build the ffmpeg arg list for reading
    /// from the helper's socket. The output side
    /// (libx264, yuv420p) is the same as the X11 path;
    /// the input side uses rawvideo over the Unix
    /// socket.</summary>
    /// <summary>
    /// Maps a protocol pixel format to the ffmpeg <c>-pix_fmt</c> value.
    ///
    /// Bgrx and Bgr0 are the same packed layout with an undefined fourth
    /// byte, which ffmpeg names "bgr0". Bgra carries a real alpha and
    /// stays "bgra". The decoded result is identical for Bgrx and Bgra
    /// because the fourth byte is discarded on the way to yuv420p, but
    /// labelling it correctly is what makes a BGRx source and a BGRA
    /// source distinguishable in a bug report - Screen capture negotiates
    /// BGRx, Window capture negotiates BGRA.
    /// </summary>
    public static string PixelFormatToFfmpegArg(PwStreamProtocol.PixelFormat format) =>
        format switch
        {
            PwStreamProtocol.PixelFormat.Bgr0 => "bgr0",
            PwStreamProtocol.PixelFormat.Bgrx => "bgr0",
            // Rgbx is R,G,B,X - the opposite of Bgrx. ffmpeg names
            // this "rgb0" (a 32-bit packed format with an undefined
            // 4th byte). The decoder produces a clean RGB image.
            PwStreamProtocol.PixelFormat.Rgbx => "rgb0",
            _ => "bgra",
        };

    private IReadOnlyList<string> BuildFfmpegArgs(PwStreamProtocol.StreamHeader header)
    {
        var pixFmt = PixelFormatToFfmpegArg(header.Format);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-progress", "pipe:1",
            "-nostats", "-stats_period", "0.1",
            // Input: rawvideo from the helper's socket.
            // The helper writes a flat stream of BGRA/BGR0
            // frames with no header (per the updated
            // protocol), so ffmpeg's rawvideo demuxer
            // picks up the format from these args.
            "-f", "rawvideo",
            "-pix_fmt", pixFmt,
            "-video_size", $"{header.Width}x{header.Height}",
            "-framerate", header.FramerateFps.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            // "unix://" is required: ffmpeg's default file protocol calls
            // open(), and open() on a Unix socket fails with ENXIO
            // ("No such device or address"), so the input never opens. The
            // unix protocol does the socket()/connect() dance instead.
            "-i", "unix://" + _socketPath,
            // Output: same as the X11 path.
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-crf", "18", "-pix_fmt", "yuv420p", "-r", _framerate.ToString(),
            "-y", OutputFile,
        };
        // Region/geometry drop: x11grab takes its region straight from
        // the device string, so a project configured on X11 captures a
        // sub-region. On Wayland the portal picks the whole output
        // instead, and dropping Source.Device silently expands the
        // capture to whatever was selected in the dialog. Apply the
        // configured crop as a filter so a region chosen on either
        // platform survives the other.
        if (_region is { } r)
            args.InsertRange(args.Count - 1, r.ToFfmpegArgs());
        return args;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _inner.StopAsync(ct).ConfigureAwait(false);
        // ffmpeg closing the socket causes the helper's
        // accept()ed client to see EOF; the helper then
        // exits naturally. Send SIGTERM and wait briefly
        // before SIGKILL.
        var helper = _helper;
        if (helper is not null && !helper.HasExited)
        {
            try { helper.StandardInput?.Close(); } catch { }
            try
            {
                if (!helper.WaitForExit(2000))
                {
                    helper.Kill(entireProcessTree: true);
                    helper.WaitForExit(1000);
                }
            }
            catch { }
        }
        // Socket cleanup is intentionally omitted: PickSocketPath
        // picks a unique per-take path and the helper created the
        // inode at startup. Unlinking here can race with a concurrent
        // recording that just chose the same counter value, and the
        // /tmp filesystem will reap the socket on its own.
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
        var helper = _helper;
        _helper = null;
        if (helper is not null)
        {
            try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
            helper.Dispose();
        }
        // Do NOT unlink the socket here. PickSocketPath generates a
        // unique per-take path with a process id and Interlocked counter,
        // and the helper creates the inode at startup; the same record
        // is also created by File.Delete in older code, which yanks the
        // socket out from under a concurrent helper bind. Letting the
        // filesystem clean it up at process exit is the only safe choice
        // for a path another recording in this process might also touch.
    }

    private async Task ReadHelperStderrAsync(StreamReader stderr, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                lock (_helperStderr)
                {
                    _helperStderr.AppendLine(line);
                    if (_helperStderr.Length > 8192)
                        _helperStderr.Remove(0, _helperStderr.Length - 8192);
                }
                // Mirror into the app log. The helper narrates the whole
                // capture on stderr - portal state, the negotiated
                // variant, stream state transitions, buffer mapping - and
                // previously all of that was kept only in _helperStderr,
                // which is read solely to build a failure message. On a
                // successful capture it was discarded, so a source that
                // "worked" but produced an empty file (a shared window
                // that never repaints emits no buffers at all) left no
                // trace anywhere and had to be reproduced by hand.
                AppLog.Current?.WriteLine("[cs-pw-helper] " + line);
                // The "CSWH W=... H=... FPS=..." line tells
                // us the PipeWire stream is up and ffmpeg
                // can connect.
                if (!_headerTcs.Task.IsCompleted)
                {
                    var header = PwStreamProtocol.TryParseHeaderLine(line);
                    if (header is not null)
                    {
                        _headerTcs.TrySetResult(header.Value);
                    }
                }
            }
        }
        catch { }
    }

    private string HelperStderrTail()
    {
        // _helperStderr is mutated under lock by the reader task.
        // StringBuilder is not thread-safe, so the read here has to
        // hold the same lock or it can throw IndexOutOfRangeException
        // when the reader is mid-append-and-trim. Both call sites
        // (the Exited handler on a ThreadPool thread and the
        // header-wait timeout) are real entry points because the
        // reader is running on a different task.
        lock (_helperStderr)
        {
            var s = _helperStderr.ToString().TrimEnd();
            return s.Length == 0 ? "(no helper output captured)" : s;
        }
    }
}
