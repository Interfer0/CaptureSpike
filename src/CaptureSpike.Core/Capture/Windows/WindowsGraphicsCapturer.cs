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
#endif

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Windows-specific window capture using
/// <c>Windows.Graphics.Capture</c> (WGC) — the modern
/// Microsoft-recommended path that captures the actual
/// GPU-composed pixels of a single window.
///
/// The window is selected by the user from the source
/// device dropdown (populated by <see cref="DeviceEnumeration.GetWindows"/>).
/// The selected HWND is stored in <c>source.Device</c>
/// as <c>"hwnd:0xABCD"</c>. There is no system picker
/// at record time — the capturer uses the stored HWND
/// directly via the IGraphicsCaptureItemInterop COM
/// interop interface.
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
    private readonly string? _deviceId;
#if WINDOWS
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
    /// uses the HWND stored in <c>deviceId</c> (from the
    /// source device dropdown) and starts capturing.
    /// On non-Windows this throws (the capturer is
    /// only registered on Windows).
    /// </summary>
    public WindowsGraphicsCapturer(
        string sourceId,
        string outputFile,
        string ffmpeg,
        int framerate,
        string? deviceId = null)
    {
        SourceId = sourceId;
        OutputFile = outputFile;
        _ffmpeg = ffmpeg;
        _framerate = framerate;
        _deviceId = deviceId;
#if !WINDOWS
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
        // Step 1: get the GraphicsCaptureItem. The
        // window is selected by the user from the source
        // device dropdown (no system picker). The HWND
        // is stored in source.Device in "hwnd:0xABCD"
        // form. Legacy entries that store a plain title
        // are looked up via FindWindow. If neither
        // resolves, we fail with a clear error so the
        // user knows to re-pick a window.
        var item = ResolveCaptureItem(ct);
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
            AppLog.Current?.WriteLine(
                $"[cs] WGC: waiting for first frame from device='{_deviceId}'...");
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
            {
                AppLog.Current?.WriteLine(
                    $"[cs] WGC: no data from device='{_deviceId}' within 10s");
                throw new CaptureException(SourceId,
                    $"No data from '{SourceId}' within 10s of starting capture.{Environment.NewLine}{ErrorTail()}");
            }
            AppLog.Current?.WriteLine(
                $"[cs] WGC: first frame from device='{_deviceId}' OK, recording started");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
#endif
    }

#if WINDOWS
    // FindWindowW: looks up an HWND by window title. Used
    // to migrate legacy palette entries that stored a
    // plain title (the gdigrab format) to an HWND that
    // WGC can use.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    // IGraphicsCaptureItemInterop: the COM interop
    // interface for creating a GraphicsCaptureItem from a
    // raw HWND. The .NET WinRT projection doesn't expose
    // this as a managed method, so we cast the runtime
    // class itself to this interface and call
    // CreateForWindow directly. The same pattern the
    // Microsoft sample uses (CaptureHelper.cs in
    // Windows.UI.Composition-Win32-Samples).
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    // GUID of the GraphicsCaptureItem runtime class.
    // We pass this as the riid to CreateForWindow so the
    // returned pointer is cast to the right WinRT type.
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// Create a <see cref="GraphicsCaptureItem"/> from a
    /// raw HWND. The .NET WinRT projection doesn't expose
    /// <c>GraphicsCaptureItem.CreateFromWindowHandle</c>
    /// as a managed method, so we use the
    /// <c>IGraphicsCaptureItemInterop</c> COM interop
    /// interface that the activation factory implements.
    ///
    /// Path: WinRT activation factory (via
    /// <c>WinRT.ActivationFactory.Get</c>) →
    /// <see cref="Marshal.QueryInterface"/> for the interop
    /// IID → call <c>CreateForWindow</c> through the COM
    /// vtable →
    /// <c>ABI.Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi</c>
    /// to wrap the raw pointer in the managed projection.
    /// </summary>
    private static GraphicsCaptureItem CreateItemFromWindow(IntPtr hwnd)
    {
        // Get the activation factory as an IObjectReference.
        // The string is the WinRT class name (not the
        // .NET projection name).
        var factoryRef = WinRT.ActivationFactory.Get(
            "Windows.Graphics.Capture.GraphicsCaptureItem");
        var factoryPtr = factoryRef.GetRef();
        try
        {
            // QueryInterface for IGraphicsCaptureItemInterop.
            // The factory is IActivationFactory (IInspectable)
            // by default; the IGraphicsCaptureItemInterop is
            // a separate interface that the same object also
            // implements. Use Marshal.QueryInterface (the
            // proper .NET wrapper for IUnknown::QueryInterface
            // — there is no direct P/Invoke entry point named
            // "QueryInterface" in ole32.dll; it's a COM
            // interface method, not a DLL export).
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.QueryInterface(factoryPtr, in interopIid, out var interopPtr);
            if (interopPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "QueryInterface for IGraphicsCaptureItemInterop returned null. " +
                    "The activation factory may not implement this interface.");
            }
            try
            {
                // Call CreateForWindow through the vtable.
                // IGraphicsCaptureItemInterop inherits IUnknown
                // (3 methods), then has CreateForWindow at
                // index 3. Calling convention is the C++
                // member-function convention: on x64 the
                // this pointer is the first arg, passed in
                // RCX. .NET's [UnmanagedFunctionPointer] for
                // COM is always StdCall. The first arg is
                // the this pointer (we pass interopPtr).
                var itemIid = GraphicsCaptureItemGuid;
                IntPtr itemPtr = CreateForWindowViaVtable(
                    interopPtr, hwnd, ref itemIid);
                if (itemPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"IGraphicsCaptureItemInterop::CreateForWindow({hwnd:X}) returned null.");
                }

                try
                {
                    return ABI.Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi(itemPtr);
                }
                catch
                {
                    Marshal.Release(itemPtr);
                    throw;
                }
            }
            finally
            {
                Marshal.Release(interopPtr);
            }
        }
        finally
        {
            factoryRef.Dispose();
        }
    }

    /// <summary>
    /// Call IGraphicsCaptureItemInterop::CreateForWindow
    /// through the vtable. Returns the new
    /// GraphicsCaptureItem IInspectable* (caller owns
    /// one ref; FromAbi or Marshal.Release must dispose
    /// it). Throws on non-S_OK HRESULT.
    /// </summary>
    private static IntPtr CreateForWindowViaVtable(
        IntPtr interopPtr, IntPtr hwnd, ref Guid iid)
    {
        // vtable[0..2] = IUnknown (QueryInterface, AddRef, Release).
        // vtable[3] = CreateForWindow.
        // vtable[4] = CreateForMonitor.
        var vtable = Marshal.ReadIntPtr(interopPtr);
        var methodPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);

        var createForWindow = (CreateForWindowDelegate)Marshal.GetDelegateForFunctionPointer(
            methodPtr, typeof(CreateForWindowDelegate));
        var hr = createForWindow(interopPtr, hwnd, ref iid, out var ppv);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR((int)hr);
        }
        return ppv;
    }

    // Delegate for IGraphicsCaptureItemInterop::CreateForWindow.
    // Signature: HRESULT CreateForWindow(
    //     HWND hwnd, REFIID iid, void** ppv);
    // .NET COM interop convention: return value is HRESULT
    // (int), first explicit arg is the this pointer
    // (we pass interopPtr).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(
        IntPtr thisPtr,
        IntPtr hwnd,
        ref Guid iid,
        out IntPtr ppv);

    private GraphicsCaptureItem ResolveCaptureItem(CancellationToken ct)
    {
        // Path 1: modern HWND form ("hwnd:0xABCD"). The
        // source dropdown stores this since the WGC
        // migration. Direct path, no user interaction,
        // no system picker.
        if (DeviceParsers.TryParseHwnd(_deviceId, out var hwnd))
        {
            return CreateItemFromWindow(hwnd);
        }

        // Path 2: legacy plain-title form (the gdigrab
        // format). The palette might have entries from
        // before this migration. Look up the title via
        // FindWindowW; if the window is still open, use
        // its current HWND. If FindWindow returns zero,
        // the window is gone.
        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            var found = FindWindowW(null, _deviceId);
            if (found != IntPtr.Zero)
            {
                return CreateItemFromWindow(found);
            }
        }

        // No HWND could be resolved. The user has to
        // re-pick a window from the source device
        // dropdown. We do NOT fall back to the system
        // WGC picker — Daniel's explicit feedback was
        // "the picker did not start, nor should it be
        // needed." The right fix is always in the
        // dropdown.
        throw new CaptureException(SourceId,
            $"Could not find the window '{_deviceId}'. " +
            "Re-open the source and pick a window from the device dropdown. " +
            "The dropdown is populated with all currently-open windows.");
    }

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
                // Also write to the app log so the user
                // can see ffmpeg output. ReadStderrAsync
                // is the only reader of the stderr pipe
                // (don't add a second Task.Run, that
                // would race for the same stream).
                AppLog.Current?.WriteLine($"[ffmpeg] {line}");
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
