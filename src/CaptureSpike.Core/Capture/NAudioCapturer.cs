#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Windows audio capture via WASAPI (NAudio 2.x). ffmpeg has no reliable
/// WASAPI-loopback input, so mic (<see cref="WasapiCapture"/>) and system
/// audio (<see cref="WasapiLoopbackCapture"/>) are captured here and written
/// as WAV. Only compiled into the Windows TFM; instantiated only on Windows.
///
/// Loopback note: WASAPI sends no data during silence, which would make the
/// capturer time out if nothing is playing, or leave gaps that drift the
/// audio earlier than the video. A silent keep-alive stream plays through
/// the same render device so the capture clock never stops.
/// </summary>
public sealed class NAudioCapturer : ISourceCapturer
{
    /// <summary>
    /// Endless silence in the device's exact format; keeps a WASAPI render
    /// stream alive so loopback capture never stalls.
    /// </summary>
    private sealed class SilenceProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; }

        public SilenceProvider(WaveFormat format) => WaveFormat = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }

    private readonly bool _loopback;
    private readonly string? _deviceId;
    private WasapiCapture? _capture;
    private MMDevice? _mmDevice;
    private WaveFileWriter? _writer;
    private IWavePlayer? _keepAlive;
    /// <summary>Human-readable name of the device we tried to open.
    /// Captured before the WASAPI call so the error message can
    /// name the actual physical device (e.g. "Headset
    /// Microphone") instead of the opaque endpoint id.</summary>
    private string? _deviceFriendlyName;
    private readonly TaskCompletionSource _firstDataTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public string SourceId { get; }
    public SourceKind Kind { get; }
    public string OutputFile { get; }
    public DateTimeOffset? FirstDataAt { get; private set; }

    public NAudioCapturer(string sourceId, SourceKind kind, string outputFile,
        bool loopback, string? deviceId = null)
    {
        SourceId = sourceId;
        Kind = kind;
        OutputFile = outputFile;
        _loopback = loopback;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    public async Task StartAsync(DateTimeOffset t0, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("NAudio capture requires Windows.");

        WasapiCapture capture;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                try
                {
                    _mmDevice = _deviceId is null
                        ? enumerator.GetDefaultAudioEndpoint(
                            _loopback ? DataFlow.Render : DataFlow.Capture, Role.Multimedia)
                        : enumerator.GetDevice(_deviceId);
                    _deviceFriendlyName = _mmDevice?.FriendlyName;
                }
                catch (Exception)
                {
                    // The id didn't resolve. Fall back to default
                    // so the user can at least record something,
                    // and capture the default's name for the error.
                    _mmDevice = enumerator.GetDefaultAudioEndpoint(
                        _loopback ? DataFlow.Render : DataFlow.Capture, Role.Multimedia);
                    _deviceFriendlyName = _mmDevice?.FriendlyName
                        ?? "<unknown device>";
                    throw;
                }
            }
            finally
            {
                enumerator.Dispose();
            }
            capture = _loopback
                ? new WasapiLoopbackCapture(_mmDevice)
                : new WasapiCapture(_mmDevice);
        }
        catch (Exception ex) when (ex is not CaptureException)
        {
            // Translate the common HRESULTs into a hint the user
            // can act on. Without this, all they see is the raw
            // error code and the opaque endpoint id.
            string hint = ex.Message switch
            {
                var m when m.Contains("0x88890008") =>
                    " — the device is in use by another application. Close Zoom/Discord/OBS and try again.",
                var m when m.Contains("0x88890004") =>
                    " — the device endpoint couldn't be created. It may have been unplugged, disabled, or its driver is in a bad state. Reconnect it or pick a different device.",
                var m when m.Contains("0x80070490") =>
                    " — the device is not in the registry or has no active drivers. Check Device Manager.",
                _ when _mmDevice is null && _deviceId is not null =>
                    $" — the saved device id '{_deviceId}' wasn't found. It was probably unplugged. Pick a different device.",
                _ => "",
            };
            var name = _deviceFriendlyName is null ? "" : $" \"{_deviceFriendlyName}\"";
            var id = _deviceId is null ? "the system default" : $"'{_deviceId}'";
            throw new CaptureException(SourceId,
                $"Could not open audio device{name} ({id}): {ex.Message}{hint}");
        }
        _capture = capture;
        _writer = new WaveFileWriter(OutputFile, capture.WaveFormat);

        // Loopback: keep the render stream alive with silence so WASAPI
        // delivers continuous data even when nothing is playing.
        if (_loopback)
        {
            WasapiOut? keepAlive = null;
            try
            {
                keepAlive = new WasapiOut(_mmDevice, AudioClientShareMode.Shared, false, 100);
                keepAlive.Init(new SilenceProvider(capture.WaveFormat));
                keepAlive.Play();
                _keepAlive = keepAlive;
            }
            catch
            {
                try { keepAlive?.Dispose(); } catch { }
                throw;
            }
        }

        capture.DataAvailable += (_, e) =>
        {
            if (FirstDataAt is null)
            {
                FirstDataAt = DateTimeOffset.UtcNow;
                _firstDataTcs.TrySetResult();
            }
            try { _writer?.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) _stoppedTcs.TrySetException(e.Exception);
            else _stoppedTcs.TrySetResult();
        };

        try
        {
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new CaptureException(SourceId, $"Could not start audio capture for '{SourceId}': {ex.Message}");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var winner = await Task.WhenAny(
                _firstDataTcs.Task, _stoppedTcs.Task, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);

            if (winner == _stoppedTcs.Task)
            {
                await _stoppedTcs.Task.ConfigureAwait(false); // rethrows device errors
                throw new CaptureException(SourceId, $"Audio capture for '{SourceId}' stopped before producing data.");
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (winner != _firstDataTcs.Task)
                throw new CaptureException(SourceId, $"No audio data from '{SourceId}' within 10s. Is the device available?");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var capture = _capture;
        if (capture is null) return;
        try { capture.StopRecording(); } catch { }
        try { await _stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch { }
        // Stop the keep-alive first so no more silence is captured.
        try { _keepAlive?.Stop(); } catch { }
        try { _keepAlive?.Dispose(); } catch { } finally { _keepAlive = null; }
        // Disposing the writer finalizes the WAV header.
        try { _writer?.Dispose(); } catch { } finally { _writer = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture?.StopRecording(); } catch { }
        try { _keepAlive?.Stop(); } catch { }
        try { _keepAlive?.Dispose(); } catch { } finally { _keepAlive = null; }
        try { _writer?.Dispose(); } catch { } finally { _writer = null; }
        try { _capture?.Dispose(); } catch { } finally { _capture = null; }
        try { _mmDevice?.Dispose(); } catch { } finally { _mmDevice = null; }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
#endif
