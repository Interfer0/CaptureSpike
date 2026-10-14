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
/// byte buffer. A fixed-rate writer feeds the latest
/// captured image to ffmpeg's rawvideo stdin, repeating it
/// when WGC has not delivered a newer frame. This preserves
/// the capture timeline even when frame delivery is sparse.
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
/// - When WGC stops delivering new frames (for example,
///   while a window is minimized or not being repainted),
///   the fixed-rate writer repeats the latest frame so
///   elapsed playback time remains correct.
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
    private readonly object _latestFrameLock = new();
    private byte[]? _latestFrame;
    private long _latestFrameGeneration;
    private Task? _frameWriterTask;
    private CancellationTokenSource? _frameWriterCts;
    private long _capturedFrameCount;
    private long _writtenFrameCount;
    private long _repeatedFrameCount;
#endif
    private Process? _process;
    private readonly List<string> _stderrTail = new();
    private readonly TaskCompletionSource _firstDataTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstCapturedFrameTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        AppLog.Current?.WriteLine(
            $"[cs] WGC: starting window capture ({width}x{height}) at {_framerate} fps");

        // Step 2: create a D3D11 device, then wrap it in
        // the WinRT IDirect3DDevice that the frame pool
        // needs. The D3D11FrameReader creates both and
        // keeps the raw ID3D11Device* for surface copies.
        var d3dDevice = D3D11FrameReader.CreateWinRtDevice(
            out var nativeDevice, out var immediateContext);
        var frameReader = new D3D11FrameReader(nativeDevice, immediateContext);
        // StartAsync can run without a WinRT dispatcher queue. The
        // dispatcher-bound Create overload may therefore never deliver
        // FrameArrived; the free-threaded pool raises it on its own thread.
        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
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

        // Step 4: subscribe to frame arrivals and keep a
        // CPU-side copy of the most recent frame. A separate
        // fixed-rate writer feeds frames to ffmpeg's stdin.
        var stdin = process.StandardInput.BaseStream;
        var frameArrivedCount = 0;
        var nullFrameCount = 0;
        var frameErrorCount = 0;
        framePool.FrameArrived += (sender, _) =>
        {
            Interlocked.Increment(ref frameArrivedCount);
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    Interlocked.Increment(ref nullFrameCount);
                    return;
                }
                byte[] pixels;
                lock (_latestFrameLock)
                {
                    (pixels, _, _) = frameReader.ReadFrame(frame);
                    _latestFrame = pixels;
                    _latestFrameGeneration++;
                    FirstDataAt ??= DateTimeOffset.UtcNow;
                }
                Interlocked.Increment(ref _capturedFrameCount);
                _firstCapturedFrameTcs.TrySetResult();
            }
            catch (Exception ex)
            {
                var errorCount = Interlocked.Increment(ref frameErrorCount);
                if (errorCount == 1)
                {
                    AppLog.Current?.WriteLine(
                        $"[cs] WGC: FrameArrived callback failed: {ex}");
                }
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
            var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);
            var winner = await Task.WhenAny(
                _firstCapturedFrameTcs.Task, _exitTcs.Task, timeoutTask).ConfigureAwait(false);

            if (winner == _exitTcs.Task)
            {
                var code = await _exitTcs.Task.ConfigureAwait(false);
                throw new CaptureException(SourceId,
                    $"ffmpeg exited (code {code}) before capturing any data from '{SourceId}'.{Environment.NewLine}{ErrorTail()}");
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (winner != _firstCapturedFrameTcs.Task)
            {
                throw new CaptureException(SourceId,
                    $"No frame from '{SourceId}' within 10s. " +
                    $"Frame events={Volatile.Read(ref frameArrivedCount)}, " +
                    $"empty={Volatile.Read(ref nullFrameCount)}, " +
                    $"callback errors={Volatile.Read(ref frameErrorCount)}." +
                    $"{Environment.NewLine}{ErrorTail()}");
            }

            var writerCts = new CancellationTokenSource();
            _frameWriterCts = writerCts;
            _frameWriterTask = Task.Run(
                () => WriteFramesAsync(stdin, writerCts.Token));
            winner = await Task.WhenAny(
                _firstDataTcs.Task, _exitTcs.Task, timeoutTask).ConfigureAwait(false);
            if (winner == _exitTcs.Task)
            {
                var code = await _exitTcs.Task.ConfigureAwait(false);
                throw new CaptureException(SourceId,
                    $"ffmpeg exited (code {code}) before capturing any data from '{SourceId}'.{Environment.NewLine}{ErrorTail()}");
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (winner != _firstDataTcs.Task)
            {
                throw new CaptureException(SourceId,
                    $"No data from '{SourceId}' within 10s of starting capture.{Environment.NewLine}{ErrorTail()}");
            }
            AppLog.Current?.WriteLine(
                $"[cs] WGC: window recording started ({width}x{height})");
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
    // raw HWND. Declared with the standard .NET COM
    // interop pattern ([ComImport] + [InterfaceType(IsIUnknown)])
    // so the .NET runtime handles vtable walking,
    // AddRef/Release, and calling convention automatically.
    //
    // The methods on IUnknown (QueryInterface, AddRef,
    // Release) are NOT declared — the runtime injects
    // them automatically.
    //
    // COM HRESULT return values: each method's
    // return value is `void` in the managed declaration
    // (NOT HRESULT). The runtime reads the actual
    // HRESULT, and throws a COMException on failure.
    // (This is the standard "HRESULT-marshalled-as-void"
    // pattern for COM interop. .NET inspects the
    // [PreserveSig] attribute to decide; default is
    // to throw on non-zero. We use the default.)
    //
    // The GUID is from windows.graphics.capture.interop.h:
    //   DECLARE_INTERFACE_IID_(IGraphicsCaptureItemInterop,
    //       IUnknown, "3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        // HRESULT CreateForWindow(HWND, REFIID, void**);
        // Returns: a new GraphicsCaptureItem* (caller owns 1 ref).
        void CreateForWindow(
            IntPtr hwnd,
            ref Guid iid,
            out IntPtr result);

        // HRESULT CreateForMonitor(HMONITOR, REFIID, void**);
        void CreateForMonitor(
            IntPtr monitor,
            ref Guid iid,
            out IntPtr result);
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
    /// <see cref="Marshal.QueryInterface"/> for the
    /// interop IID → <see cref="Marshal.GetTypedObjectForIUnknown"/>
    /// to wrap the pointer in the managed [ComImport]
    /// interface → standard .NET COM interop call to
    /// <c>CreateForWindow</c> → <see cref="ABI.Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi"/>
    /// to wrap the raw pointer in the managed projection.
    ///
    /// The .NET COM interop layer handles all the vtable
    /// work, calling convention, and AddRef/Release. We
    /// never touch the vtable manually.
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
            // proper .NET wrapper for IUnknown::QueryInterface).
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
                // Wrap the raw IUnknown* in the managed
                // [ComImport] interface. .NET's COM
                // interop layer will then route method
                // calls through the right vtable entries
                // and handle AddRef/Release correctly.
                var interop =
                    (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                        interopPtr, typeof(IGraphicsCaptureItemInterop));
                var itemIid = GraphicsCaptureItemGuid;
                interop.CreateForWindow(hwnd, ref itemIid, out var itemPtr);
                if (itemPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"IGraphicsCaptureItemInterop::CreateForWindow({hwnd:X}) returned null.");
                }

                try
                {
                    // ABI.Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi
                    // takes a raw IntPtr (an IInspectable*) and
                    // returns the projected managed
                    // GraphicsCaptureItem. This is the standard
                    // CsWinRT path; using it directly is the
                    // documented way to wrap a raw WinRT pointer
                    // without going through a public factory method.
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
                // GetTypedObjectForIUnknown calls AddRef on
                // the returned RCW. Release the raw
                // interface pointer here so the underlying
                // COM object is only alive via the RCW.
                Marshal.Release(interopPtr);
            }
        }
        finally
        {
            factoryRef.Dispose();
        }
    }

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
            // libx264's yuv420p output requires even dimensions. WGC can
            // report odd-sized windows, so pad the right/bottom edge
            // rather than cropping captured pixels.
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
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
        await StopCaptureAndWriterAsync().ConfigureAwait(false);
        AppLog.Current?.WriteLine(
            $"[cs] WGC: frames captured={Interlocked.Read(ref _capturedFrameCount)}, " +
            $"written={Interlocked.Read(ref _writtenFrameCount)} " +
            $"(repeated={Interlocked.Read(ref _repeatedFrameCount)})");
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
        await StopCaptureAndWriterAsync().ConfigureAwait(false);
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

#if WINDOWS
    private async Task StopCaptureAndWriterAsync()
    {
        // Stop WGC first so the latest-frame buffer cannot be replaced
        // while the fixed-rate writer is being shut down.
        _sessionDisposable?.Dispose();
        _sessionDisposable = null;
        _framePoolDisposable?.Dispose();
        _framePoolDisposable = null;

        var writerCts = _frameWriterCts;
        _frameWriterCts = null;
        writerCts?.Cancel();
        var writer = _frameWriterTask;
        _frameWriterTask = null;
        if (writer is not null)
        {
            try { await writer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        writerCts?.Dispose();
    }

    private async Task WriteFramesAsync(Stream stdin, CancellationToken ct)
    {
        long frameIndex = 0;
        long lastGeneration = -1;
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            while (true)
            {
                var targetTimestamp = startTimestamp +
                    frameIndex * Stopwatch.Frequency / _framerate;
                var remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
                if (remainingTicks > 0)
                {
                    var delay = TimeSpan.FromSeconds(
                        (double)remainingTicks / Stopwatch.Frequency);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                byte[]? pixels;
                long generation;
                lock (_latestFrameLock)
                {
                    pixels = _latestFrame;
                    generation = _latestFrameGeneration;
                }
                if (pixels is null)
                    continue;

                await stdin.WriteAsync(pixels.AsMemory(), ct).ConfigureAwait(false);
                MarkFirstData();
                Interlocked.Increment(ref _writtenFrameCount);
                if (generation == lastGeneration)
                    Interlocked.Increment(ref _repeatedFrameCount);
                else
                    lastGeneration = generation;
                frameIndex++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Current?.WriteLine(
                $"[cs] WGC: fixed-rate frame writer failed: {ex}");
        }
    }
#endif

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
