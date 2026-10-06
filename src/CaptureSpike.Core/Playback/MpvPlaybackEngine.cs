using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using CaptureSpike.Core.Capture;

namespace CaptureSpike.Core.Playback;

/// <summary>
/// Plays a take through an mpv subprocess controlled over JSON IPC.
/// All source files are loaded at once; a lavfi filter graph (see
/// <see cref="FilterGraphBuilder"/>) aligns them by their take.json offsets
/// and composites the scene layout. Muting/hiding a track rebuilds the
/// graph and restarts mpv at the previous position.
/// </summary>
public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private readonly string? _mpvPathOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _pollTimer;

    private Process? _process;
    private MpvIpcClient? _ipc;
    private StringBuilder? _stderr;

    private Project? _project;
    private Scene? _scene;
    private string? _takeDir;
    private Take? _take;
    /// <summary>Single-file playback mode (play one source file, no filter graph).</summary>
    private string? _singleFile;
    private double _singleFileDurationSec;
    private readonly HashSet<string> _muted = new(StringComparer.Ordinal);

    private double _positionSec;
    private bool _paused = true;
    private bool _loaded;
    private int _polling;

    public MpvPlaybackEngine(string? mpvPathOverride = null)
    {
        _mpvPathOverride = mpvPathOverride;
        _pollTimer = new Timer(PollState, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Native window handle mpv should render into (0 = its own window).</summary>
    public nint EmbedWindowHandle { get; set; }

    public TimeSpan Position => TimeSpan.FromSeconds(Math.Max(0, _positionSec));

    public TimeSpan Duration =>
        _take is not null ? TimeSpan.FromSeconds(Math.Max(0, _take.DurationSeconds)) :
        _singleFile is not null ? TimeSpan.FromSeconds(Math.Max(0, _singleFileDurationSec)) :
        TimeSpan.Zero;

    public bool IsPlaying => _loaded && !_paused;

    /// <summary>Raised when mpv reports end-of-file.</summary>
    public event Action? PlaybackEnded;

    public async Task LoadAsync(Project project, Scene scene, string takeDir, Take take, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            _project = project;
            _scene = scene;
            _takeDir = takeDir;
            _take = take;
            _singleFile = null;
            _muted.Clear();
            await StartMpvAsync(positionSec: 0, startPaused: false, ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Plays a single media file (e.g. one source's MP4/WAV from a take folder)
    /// without the take's filter graph. Muting does not apply in this mode.
    /// </summary>
    public async Task LoadSingleFileAsync(string filePath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            _project = null;
            _scene = null;
            _takeDir = null;
            _take = null;
            _singleFile = filePath;
            _singleFileDurationSec = 0;
            _muted.Clear();
            await StartMpvAsync(positionSec: 0, startPaused: false, ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    public async Task PlayAsync(CancellationToken ct = default) =>
        await SetPausedAsync(false, ct).ConfigureAwait(false);

    public async Task PauseAsync(CancellationToken ct = default) =>
        await SetPausedAsync(true, ct).ConfigureAwait(false);

    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        var ipc = RequireIpc();
        await ipc.CommandAsync(["seek", position.TotalSeconds, "absolute"], ct).ConfigureAwait(false);
        _positionSec = position.TotalSeconds;
    }

    public async Task SetTrackMutedAsync(string sourceId, bool muted, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_take is null) return;
            if (muted) _muted.Add(sourceId); else _muted.Remove(sourceId);
            if (!_loaded) return;

            if (_muted.Count >= _take.Sources.Count)
                throw new InvalidOperationException("Cannot mute every source in the take.");

            var pos = _positionSec;
            var wasPaused = _paused;
            await StartMpvAsync(pos, wasPaused, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // ----- internals -----

    private MpvIpcClient RequireIpc() =>
        _ipc ?? throw new InvalidOperationException("No take is loaded.");

    private async Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        var ipc = RequireIpc();
        await ipc.SetPropertyAsync("pause", paused, ct).ConfigureAwait(false);
        _paused = paused;
    }

    private async Task StartMpvAsync(double positionSec, bool startPaused, CancellationToken ct)
    {
        await StopInternalAsync().ConfigureAwait(false);

        var mpv = ToolPaths.Resolve("mpv", _mpvPathOverride);

        List<string> files;
        string? graph = null;
        if (_take is not null)
        {
            var take = _take;
            files = take.Sources.Select(s => Path.Combine(_takeDir!, s.File)).ToList();
            var missing = files.FirstOrDefault(f => !File.Exists(f));
            if (missing is not null)
                throw new FileNotFoundException($"Take media file is missing: {missing}");

            graph = FilterGraphBuilder.Build(_project!, _scene!, take, _muted);
        }
        else if (_singleFile is not null)
        {
            if (!File.Exists(_singleFile))
                throw new FileNotFoundException($"Media file is missing: {_singleFile}");
            files = new List<string> { _singleFile };
        }
        else
        {
            throw new InvalidOperationException("Nothing to play: no take or file was loaded.");
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var sockId = Guid.NewGuid().ToString("N");
        string ipcArg;
        string? socketPath = null;
        string? pipeName = null;
        if (isWindows)
        {
            pipeName = $"cs-mpv-{sockId}";
            ipcArg = $@"\\.\pipe\{pipeName}";
        }
        else
        {
            socketPath = Path.Combine(Path.GetTempPath(), $"cs-mpv-{sockId}.sock");
            ipcArg = socketPath;
        }

        var psi = new ProcessStartInfo
        {
            FileName = mpv,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--no-terminal");
        psi.ArgumentList.Add("--msg-level=all=no");
        psi.ArgumentList.Add($"--input-ipc-server={ipcArg}");
        if (graph is not null)
            psi.ArgumentList.Add($"--lavfi-complex={graph}");
        psi.ArgumentList.Add("--keep-open=no");
        psi.ArgumentList.Add("--osd-level=0");
        psi.ArgumentList.Add(startPaused ? "--pause" : "--no-pause");
        if (EmbedWindowHandle != 0)
            psi.ArgumentList.Add($"--wid={EmbedWindowHandle}");
        foreach (var f in files)
            psi.ArgumentList.Add(f);

        _stderr = new StringBuilder();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _stderr.AppendLine(e.Data); };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start mpv.");
        process.BeginErrorReadLine();
        // Drain stdout so mpv never blocks on a full pipe.
        _ = Task.Run(async () => { try { await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false); } catch { } });

        try
        {
            _ipc = isWindows
                ? await WaitForPipeAsync(pipeName!, ct).ConfigureAwait(false)
                : await WaitForSocketAsync(socketPath!, ct).ConfigureAwait(false);
        }
        catch
        {
            await KillProcessAsync(process).ConfigureAwait(false);
            throw;
        }

        _process = process;
        _ipc.EventReceived += OnMpvEvent;

        if (positionSec > 0.5)
            await _ipc.CommandAsync(["seek", positionSec, "absolute"], ct).ConfigureAwait(false);
        _positionSec = positionSec;
        _paused = startPaused;
        _pollTimer.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

        // Fail fast if mpv rejected the startup: it exits within a second or two.
        await Task.Delay(800, ct).ConfigureAwait(false);
        if (process.HasExited && process.ExitCode != 0)
        {
            var err = _stderr.ToString().Trim();
            await StopInternalAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"mpv exited during startup (code {process.ExitCode}).{(err.Length > 0 ? " mpv said: " + err[^Math.Min(err.Length, 500)..] : "")}");
        }
        _loaded = true;
    }

    private void OnMpvEvent(System.Text.Json.JsonElement ev)
    {
        if (ev.TryGetProperty("event", out var name) && name.GetString() == "end-file")
            PlaybackEnded?.Invoke();
    }

    private async void PollState(object? _)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            var ipc = _ipc;
            if (ipc is null || _process?.HasExited == true) return;
            var pos = await ipc.GetPropertyAsync<double?>("time-pos").ConfigureAwait(false);
            if (pos.HasValue) _positionSec = pos.Value;
            var paused = await ipc.GetPropertyAsync<bool?>("pause").ConfigureAwait(false);
            if (paused.HasValue) _paused = paused.Value;
            if (_take is null && _singleFile is not null)
            {
                var dur = await ipc.GetPropertyAsync<double?>("duration").ConfigureAwait(false);
                if (dur.HasValue) _singleFileDurationSec = dur.Value;
            }
        }
        catch { /* transient: next tick retries */ }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private static async Task<MpvIpcClient> WaitForSocketAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path) || Directory.Exists(path))
            {
                try { return await MpvIpcClient.ConnectUnixAsync(path, ct).ConfigureAwait(false); }
                catch (SocketException) { /* not accepting yet */ }
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for mpv's IPC socket.");
    }

    private static async Task<MpvIpcClient> WaitForPipeAsync(string pipeName, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { return await MpvIpcClient.ConnectPipeAsync(pipeName, ct).ConfigureAwait(false); }
            catch (Exception ex) { last = ex; }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for mpv's IPC pipe.", last);
    }

    private async Task StopInternalAsync()
    {
        _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        var ipc = _ipc;
        _ipc = null;
        if (ipc is not null)
        {
            try { await ipc.CommandAsync(["quit"]).ConfigureAwait(false); } catch { }
            await ipc.DisposeAsync().ConfigureAwait(false);
        }
        var process = _process;
        _process = null;
        if (process is not null)
            await KillProcessAsync(process).ConfigureAwait(false);
        _loaded = false;
    }

    private static async Task KillProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                var exited = await Task.Run(() => process.WaitForExit(3000)).ConfigureAwait(false);
                if (!exited) process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync().ConfigureAwait(false);
        _pollTimer.Dispose();
        _gate.Dispose();
    }
}
