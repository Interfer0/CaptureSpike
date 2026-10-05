#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Windows audio capture via WASAPI (NAudio 2.x). ffmpeg has no reliable
/// WASAPI-loopback input, so mic (<see cref="WasapiCapture"/>) and system
/// audio (<see cref="WasapiLoopbackCapture"/>) are captured here and written
/// as WAV. Only compiled into the Windows TFM; instantiated only on Windows.
/// </summary>
public sealed class NAudioCapturer : ISourceCapturer
{
    private readonly bool _loopback;
    private readonly string? _deviceId;
    private WasapiCapture? _capture;
    private MMDevice? _mmDevice;
    private WaveFileWriter? _writer;
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
                _mmDevice = _deviceId is null
                    ? enumerator.GetDefaultAudioEndpoint(
                        _loopback ? DataFlow.Render : DataFlow.Capture, Role.Multimedia)
                    : enumerator.GetDevice(_deviceId);
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
            throw new CaptureException(SourceId,
                $"Could not open audio device '{_deviceId ?? "default"}': {ex.Message}");
        }
        _capture = capture;
        _writer = new WaveFileWriter(OutputFile, capture.WaveFormat);

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
        // Disposing the writer finalizes the WAV header.
        try { _writer?.Dispose(); } catch { } finally { _writer = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture?.StopRecording(); } catch { }
        try { _writer?.Dispose(); } catch { } finally { _writer = null; }
        try { _capture?.Dispose(); } catch { } finally { _capture = null; }
        try { _mmDevice?.Dispose(); } catch { } finally { _mmDevice = null; }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
#endif
