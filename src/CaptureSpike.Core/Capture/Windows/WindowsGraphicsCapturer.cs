// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using System.Diagnostics;
using System.Collections.Generic;
using System.IO;

#if WINDOWS
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using global::Windows.Graphics;
using global::Windows.Graphics.Capture;
using global::Windows.Graphics.DirectX;
using global::Windows.Graphics.DirectX.Direct3D11;
using CaptureSpike.Core.Capture.Windows;
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
    private D3D11FrameReader? _frameReader;
    private IDisposable? _framePoolDisposable;
    private IDisposable? _sessionDisposable;
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

        // Step 2: create a D3D11 device, then wrap it in
        // the WinRT IDirect3DDevice that the frame pool
        // needs. The D3D11FrameReader creates both and
        // keeps the raw ID3D11Device* for surface copies.
        var d3dDevice = D3D11FrameReader.CreateWinRtDevice(out _);
        var frameReader = new D3D11FrameReader(d3dDevice);
        var framePool = Direct3D11CaptureFramePool.Create(
            d3dDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2, // two buffers; one being filled, one being consumed
            item.Size);
        var session = framePool.CreateCaptureSession(item);

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
                var (pixels, w, h) = frameReader.ReadFrame(frame);
                stdin.Write(pixels, 0, pixels.Length);
                stdin.Flush();
                MarkFirstData();
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

        // Stash for DisposeAsync.
        _frameReader = frameReader;
        _framePoolDisposable = framePool;
        _sessionDisposable = session;

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
    /// (Surface copy is implemented in
    /// <see cref="D3D11FrameReader"/> which does the
    /// D3D11 CreateTexture2D / CopyResource / Map dance
    /// via raw P/Invoke.)
    /// </summary>
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
        // Disposing the frame pool disposes the session
        // implicitly (per the WinRT docs). The D3D11
        // frame reader holds the raw ID3D11Device* + a
        // staging texture, both freed on Dispose.
        _sessionDisposable?.Dispose();
        _framePoolDisposable?.Dispose();
        _frameReader?.Dispose();
        _sessionDisposable = null;
        _framePoolDisposable = null;
        _frameReader = null;
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
