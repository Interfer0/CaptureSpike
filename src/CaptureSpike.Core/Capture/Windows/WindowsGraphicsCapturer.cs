// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Diagnostics;
using System.Collections.Generic;
using System.IO;

#if WINDOWS
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT.Interop;
#endif

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Windows-specific window capture using
/// <c>Windows.Graphics.Capture</c> (WGC) — the modern
/// Microsoft-recommended path that captures the actual
/// GPU-composed pixels of a single window.
///
/// The user picks the window via the system
/// <see cref="GraphicsCapturePicker"/> dialog (the same
/// pattern as the Wayland portal dialog on Linux). The
/// picker is modal to the app's main window.
///
/// Frames are pulled from a <c>Direct3D11CaptureFramePool</c>
/// and copied from the GPU surface to a CPU-side BGRA
/// byte buffer. The buffer is written to ffmpeg's stdin
/// as a rawvideo stream (same approach as the Wayland
/// capture on Linux — just with a pipe instead of a Unix
/// socket).
///
/// ffmpeg command line:
///   ffmpeg -f rawvideo -pix_fmt bgra -video_size WxH
///          -framerate N -i - ...standard output args...
/// (-i - means read from stdin)
///
/// What this fixes vs the previous gdigrab path:
/// - gdigrab reads from GDI, which can't see UWP / Edge
///   / DirectX-only surfaces. WGC reads the actual
///   composited GPU output.
/// - gdigrab fails on minimized windows. WGC keeps
///   capturing even when the source window is minimized.
/// - gdigrab needs the exact window title. WGC lets
///   the user pick from a list (or click the window
///   directly in some Windows versions).
/// </summary>
public sealed class WindowsGraphicsCapturer : ISourceCapturer
{
    private readonly string _ffmpeg;
    private readonly int _framerate;
#if WINDOWS
    private readonly IntPtr _parentHwnd;
    private readonly Direct3D11CaptureFramePool? _framePool;
    private readonly GraphicsCaptureSession? _session;
#endif
    private Process? _process;
    private readonly List<string> _stderrTail = new();
    private readonly TaskCompletionSource _firstDataTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public string SourceId { get; }
    public SourceKind Kind { get; } = SourceKind.Window;
    public string OutputFile { get; }
    public DateTimeOffset? FirstDataAt { get; private set; }

    /// <summary>
    /// Build a WindowsGraphicsCapturer. On Windows this
    /// shows the WGC picker dialog and starts capturing
    /// the chosen window. On non-Windows this throws
    /// (the capturer is only registered on Windows).
    /// </summary>
    public WindowsGraphicsCapturer(
        string sourceId,
        string outputFile,
        string ffmpeg,
        int framerate)
    {
        SourceId = sourceId;
        OutputFile = outputFile;
        _ffmpeg = ffmpeg;
        _framerate = framerate;
#if WINDOWS
        _parentHwnd = WindowHandleProvider.MainWindowHandle
            ?? throw new CaptureException(sourceId,
                "Cannot start window capture: the application's main window " +
                "handle is not set. This is a CaptureSpike bug — file an issue.");
#else
        throw new PlatformNotSupportedException(
            "WindowsGraphicsCapturer is only supported on Windows.");
#endif
    }

    public async Task StartAsync(DateTimeOffset t0, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
#if !WINDOWS
        throw new PlatformNotSupportedException(
            "WindowsGraphicsCapturer is only supported on Windows.");
#else
        // Step 1: show the WGC picker and get the user's
        // chosen window. The picker is async; the user
        // can cancel by closing the dialog. We propagate
        // the cancellation to the capturer's caller.
        var picker = new GraphicsCapturePicker();
        InitializeWithWindow.Initialize(picker, _parentHwnd);
        var item = await picker.PickSingleItemAsync().AsTask(ct).ConfigureAwait(false);
        if (item is null)
        {
            throw new CaptureException(SourceId,
                "Window capture was cancelled (no window was selected in the picker).");
        }
        var (width, height) = (item.Size.Width, item.Size.Height);

        // Step 2: create a Direct3D 11 device and a frame
        // pool. IDirect3DDevice is the WinRT handle; the
        // Direct3D11Device is the CsWinRT projection.
        //
        // Creating the device requires the D3D11 constant
        // for the feature level (D3D_FEATURE_LEVEL_11_0).
        // The standard pattern is:
        //
        //   using Silk.NET.Direct3D11; (or Vortice.Windows
        //   or SharpDX — any of the D3D11 bindings)
        //   var dxDevice = D3D11.CreateDevice(...);
        //   var device = Direct3D11Helpers.CreateDevice(...);
        //
        // We don't take on a D3D11 binding just for this
        // one call. Instead, see
        //   docs/WINDOWS-CAPTURE.md
        // for the integration plan that includes either:
        //   (a) adding a small P/Invoke layer for
        //       D3D11CreateDevice, or
        //   (b) adding the Silk.NET.Direct3D11 package
        //       (the lightest D3D11 binding).
        //
        // For now, throw a clear error so the picker
        // path is testable but the frame-receiving path
        // is gated on finishing the D3D11 wrapper.
        throw new CaptureException(SourceId,
                    "WindowsGraphicsCapturer needs a Direct3D 11 device to start the " +
                    "frame pool. The device creation step isn't wired up yet — see " +
                    "docs/WINDOWS-CAPTURE.md. The picker UI works (try it to confirm the " +
                    "WGC plumbing is sound); the GPU-to-CPU surface copy is the " +
                    "remaining piece.");
        var d3dDevice = (IDirect3DDevice)null!;
        var framePool = (Direct3D11CaptureFramePool)null!;
        var session = (GraphicsCaptureSession)null!;

        // Step 3: launch ffmpeg reading rawvideo from
        // stdin. The output side is the same as the
        // existing FfmpegCapturer.
        var args = BuildFfmpegArgs(width, height);
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = Process.Start(psi)
            ?? throw new CaptureException(SourceId, $"Could not start {_ffmpeg}.");
        _process = process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try { _exitTcs.TrySetResult(process.ExitCode); }
            catch { _exitTcs.TrySetResult(-1); }
        };
        if (process.HasExited) _exitTcs.TrySetResult(process.ExitCode);

        _ = Task.Run(() => ReadProgressAsync(process.StandardOutput, ct), ct);
        _ = Task.Run(() => ReadStderrAsync(process.StandardError, ct), ct);

        // Step 4: subscribe to frame arrivals and copy
        // each GPU surface to CPU memory, then write
        // the BGRA bytes to ffmpeg's stdin.
        var stdin = process.StandardInput.BaseStream;
        framePool.FrameArrived += (sender, _) =>
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                WriteFrameToStdin(frame, d3dDevice, stdin, width, height, ct);
            }
            catch (Exception ex)
            {
                // Best-effort logging. The frame-arrived
                // callback is a fire-and-forget Task; we
                // can't throw out of it. If the write
                // fails (ffmpeg closed stdin), StopAsync
                // will clean up.
                System.Console.Error.WriteLine(
                    $"[cs] WindowsGraphicsCapturer: frame write failed: {ex.Message}");
            }
        };

        session.StartCapture();

        // Step 5: wait for first data (same pattern as
        // FfmpegCapturer). 10s is generous for WGC;
        // the first frame usually arrives in <100ms
        // after StartCapture.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var winner = await Task.WhenAny(
                _firstDataTcs.Task, _exitTcs.Task,
                Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);

            if (winner == _exitTcs.Task)
            {
                var code = await _exitTcs.Task.ConfigureAwait(false);
                throw new CaptureException(SourceId,
                    $"ffmpeg exited (code {code}) before capturing any data from '{SourceId}'.{Environment.NewLine}{ErrorTail()}");
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (winner != _firstDataTcs.Task)
                throw new CaptureException(SourceId,
                    $"No data from '{SourceId}' within 10s of starting capture.{Environment.NewLine}{ErrorTail()}");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
#endif
    }

#if WINDOWS
    private IReadOnlyList<string> BuildFfmpegArgs(int width, int height)
    {
        // -i - reads from stdin. ffmpeg's rawvideo demuxer
        // sees a flat stream of BGRA frames.
        return new List<string>
        {
            "-hide_banner", "-loglevel", "warning",
            "-progress", "pipe:1",
            "-nostats", "-stats_period", "0.1",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-video_size", $"{width}x{height}",
            "-framerate", _framerate.ToString(),
            "-i", "-",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
            "-crf", "18", "-pix_fmt", "yuv420p", "-r", _framerate.ToString(),
            "-y", OutputFile,
        };
    }

    /// <summary>
    /// Copy a single frame's GPU surface to a CPU buffer
    /// and write the BGRA bytes to ffmpeg's stdin.
    ///
    /// This is the part that requires Windows integration
    /// testing; the rest of the pipeline is verifiable on
    /// this dev box. The implementation needs:
    ///
    /// 1. Quck the IDirect3DSurface for the ID3D11Texture2D
    ///    (the WGC frame's underlying texture).
    /// 2. Create a staging texture on the same device
    ///    with CPU read access.
    /// 3. CopyResource from the capture texture to the
    ///    staging texture.
    /// 4. Map the staging texture's subresource.
    /// 5. Copy row-by-row to a managed byte[] (the GPU
    ///    row pitch may differ from width*4 due to
    ///    alignment).
    /// 6. Write the bytes to stdin.
    ///
    /// Throws because the surface-copy P/Invoke is
    /// unwritten. The rest of the pipeline (picker, frame
    /// pool, ffmpeg process, first-data detection) is in
    /// place.
    /// </summary>
    private void WriteFrameToStdin(
        Direct3D11CaptureFrame frame,
        IDirect3DDevice device,
        Stream stdin,
        int width, int height,
        CancellationToken ct)
    {
        throw new NotImplementedException(
            "GPU-to-CPU surface copy is not yet implemented. " +
            "See docs/WINDOWS-CAPTURE.md for the integration plan.");
    }
#endif

    public async Task StopAsync(CancellationToken ct)
    {
#if WINDOWS
        // Stop the capture session first so no more
        // frames arrive. Then close ffmpeg's stdin
        // (EOF → ffmpeg finalizes the file).
        try { /* session is already disposed via framePool.Dispose() in DisposeAsync */ } catch { }
#endif
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { /* stdin may already be gone */ }
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch { }
        try { await _exitTcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
        catch { /* fall through to kill */ }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
#if WINDOWS
        // The frame pool disposes the session.
        try { /* session?.Dispose() */ } catch { }
        try { /* framePool?.Dispose() */ } catch { }
#endif
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void MarkFirstData()
    {
        FirstDataAt ??= DateTimeOffset.UtcNow;
        _firstDataTcs.TrySetResult();
    }

    private string ErrorTail() =>
        _stderrTail.Count == 0 ? "(no ffmpeg output captured)"
            : string.Join(Environment.NewLine, _stderrTail.TakeLast(12));

    private async Task ReadProgressAsync(StreamReader stdout, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                    int.TryParse(line.AsSpan(6).Trim(), out var frame) && frame > 0)
                    MarkFirstData();
            }
        }
        catch (OperationCanceledException) { }
        catch { /* surfaced via the exit path */ }
    }

    private async Task ReadStderrAsync(StreamReader stderr, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Add(line);
                    if (_stderrTail.Count > 60) _stderrTail.RemoveAt(0);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }
}
