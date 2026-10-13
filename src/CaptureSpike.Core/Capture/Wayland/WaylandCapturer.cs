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
public sealed class WaylandCapturer : ISourceCapturer
{
    private readonly string _helperPath;
    private readonly string _socketPath;
    private readonly int _framerate;
    private readonly int _sourceType;
    private readonly bool _includeCursor;
    private readonly FfmpegCapturer _inner;
    private Process? _helper;
    private readonly StringBuilder _helperStderr = new();
    private readonly TaskCompletionSource<PwStreamProtocol.StreamHeader> _headerTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

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
        bool includeCursor = false)
    {
        SourceId = sourceId;
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
    private IReadOnlyList<string> BuildFfmpegArgs(PwStreamProtocol.StreamHeader header)
    {
        var pixFmt = header.Format == PwStreamProtocol.PixelFormat.Bgr0
            ? "bgr0" : "bgra";
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
            "-i", _socketPath,
            // Output: same as the X11 path.
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-crf", "18", "-pix_fmt", "yuv420p", "-r", _framerate.ToString(),
            "-y", OutputFile,
        };
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
        try { File.Delete(_socketPath); } catch { }
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
        try { File.Delete(_socketPath); } catch { }
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
        var s = _helperStderr.ToString().TrimEnd();
        return s.Length == 0 ? "(no helper output captured)" : s;
    }
}
