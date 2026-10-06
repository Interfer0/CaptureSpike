using System.Diagnostics;

namespace CaptureSpike.Core.Capture;

/// <summary>
/// Records one source by driving an ffmpeg subprocess.
///
/// First-data detection uses <c>-progress pipe:1</c> with
/// <c>-stats_period 0.1</c>: the first <c>frame=N&gt;0</c> (video) or
/// <c>out_time_ms&gt;0</c> (audio) line marks when this source really started
/// producing data, accurate to ~0.1s. If ffmpeg dies before that, Start fails
/// fast with the device error instead of recording nothing.
/// Stopping sends "q" so ffmpeg finalizes the file.
/// </summary>
public sealed class FfmpegCapturer : ISourceCapturer
{
    private readonly string _ffmpeg;
    private readonly IReadOnlyList<string> _arguments;
    private readonly bool _isVideo;
    private Process? _process;
    private readonly List<string> _stderrTail = new();
    private readonly TaskCompletionSource _firstDataTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public string SourceId { get; }
    public SourceKind Kind { get; }
    public string OutputFile { get; }
    public DateTimeOffset? FirstDataAt { get; private set; }

    public FfmpegCapturer(string sourceId, SourceKind kind, string outputFile, string ffmpeg, IReadOnlyList<string> arguments)
    {
        SourceId = sourceId;
        Kind = kind;
        OutputFile = outputFile;
        _ffmpeg = ffmpeg;
        _arguments = arguments;
        _isVideo = kind.IsVideo();
    }

    public async Task StartAsync(DateTimeOffset t0, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in _arguments) psi.ArgumentList.Add(a);

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

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var winner = await Task.WhenAny(
                _firstDataTcs.Task, _exitTcs.Task, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);

            if (winner == _exitTcs.Task)
            {
                var code = await _exitTcs.Task.ConfigureAwait(false);
                throw new CaptureException(SourceId,
                    $"ffmpeg exited (code {code}) before capturing any data from '{SourceId}'.{Environment.NewLine}{ErrorTail()}");
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (winner != _firstDataTcs.Task)
                throw new CaptureException(SourceId,
                    $"No data from '{SourceId}' within 10s of starting ffmpeg.{Environment.NewLine}{ErrorTail()}");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch { /* stdin may already be gone */ }

        try { await _exitTcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); }
        catch { /* fall through to kill */ }

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
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
                if (_isVideo)
                {
                    if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                        int.TryParse(line.AsSpan(6).Trim(), out var frame) && frame > 0)
                        MarkFirstData();
                }
                else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                         long.TryParse(line.AsSpan(12).Trim(), out var ms) && ms > 0)
                {
                    MarkFirstData();
                }
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
