// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

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
public sealed class MpvPlaybackEngine : IPlaybackEngine, IPlayer
{
    private readonly string? _mpvPathOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _pollTimer;

    private Process? _process;
    private MpvIpcClient? _ipc;
    private TaskCompletionSource? _fileLoadedTcs;
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

    /// <summary>
    /// When true, mpv holds the last frame instead of exiting at end-of-file.
    /// Used by multi-window playback so shorter sources wait for longer ones.
    /// </summary>
    public bool KeepOpen { get; set; }

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
            _tiledGraph = null;
            _tiledFiles = null;
            _singleFile = null;
            _muted.Clear();
            await StartMpvAsync(positionSec: 0, startPaused: false, ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Plays a take's sources through a single mpv instance with
    /// a caller-supplied --lavfi-complex graph. One audio clock
    /// for every source — no per-process drift. Used by the
    /// tiled-take player.
    /// </summary>
    public async Task LoadAsync(
        IReadOnlyList<string> sourceFilePaths,
        IReadOnlyList<TakeSource> takeSources,
        string lavfiGraph,
        double takeDurationSeconds,
        ISet<string>? mutedSourceIds = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            _project = null;
            _scene = null;
            _takeDir = null;
            // We still need a Take-shaped object for the muted
            // list / sources list. Use a synthetic take with the
            // provided sources. DurationSeconds is used for the
            // playhead. The fields the engine reads are Sources
            // and DurationSeconds.
            _take = new Take
            {
                Id = "tiled",
                SceneId = "tiled",
                Number = 0,
                DurationSeconds = takeDurationSeconds,
                Sources = takeSources.ToList(),
            };
            _singleFile = null;
            _muted.Clear();
            if (mutedSourceIds is not null)
                _muted.UnionWith(mutedSourceIds);
            // Inject the caller's graph and the file paths into
            // the launch path. We do this by re-using
            // StartMpvAsync after temporarily stashing the graph
            // on a field that StartMpvAsync reads.
            _tiledGraph = lavfiGraph;
            _tiledFiles = sourceFilePaths.ToList();
            await StartMpvAsync(positionSec: 0, startPaused: false, ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    // ----- begin "tiled" mode (single-mpv with caller graph) -----
    // Set by LoadAsync(sourceFilePaths, takeSources, lavfiGraph, ...)
    // immediately before StartMpvAsync. Read by StartMpvAsync
    // instead of the project/scene/take-driven graph build.
    private string? _tiledGraph;
    private List<string>? _tiledFiles;
    private bool IsTiledLaunch => _tiledGraph is not null && _tiledFiles is not null;
    // ----- end "tiled" mode -----

    /// <summary>
    /// Plays a single media file (e.g. one source's MP4/WAV from a take folder)
    /// without the take's filter graph. Muting does not apply in this mode.
    /// </summary>
    public async Task LoadSingleFileAsync(string filePath, bool startPaused = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
            _project = null;
            _scene = null;
            _takeDir = null;
            _take = null;
            _tiledGraph = null;
            _tiledFiles = null;
            _singleFile = filePath;
            _singleFileDurationSec = 0;
            _muted.Clear();
            await StartMpvAsync(positionSec: 0, startPaused: startPaused, ct).ConfigureAwait(false);
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Mutes/unmutes mpv's audio output (set_property mute).</summary>
    public async Task SetMutedAsync(bool muted, CancellationToken ct = default)
    {
        var ipc = RequireIpc();
        await ipc.SetPropertyAsync("mute", muted, ct).ConfigureAwait(false);
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
            var wasMuted = _muted.Contains(sourceId);
            if (muted) _muted.Add(sourceId); else _muted.Remove(sourceId);
            if (_muted.Count >= _take.Sources.Count)
            {
                if (wasMuted) _muted.Add(sourceId); else _muted.Remove(sourceId);
                throw new InvalidOperationException("Cannot mute every source in the take.");
            }
            if (!_loaded) return;

            if (IsTiledLaunch)
            {
                _tiledGraph = FilterGraphBuilder.BuildTakeGraph(_take.Sources, _muted);
                var response = await RequireIpc()
                    .CommandAsync(["set_property", "lavfi-complex", _tiledGraph], ct)
                    .ConfigureAwait(false);
                if (!response.TryGetProperty("error", out var error) ||
                    error.GetString() != "success")
                {
                    if (wasMuted) _muted.Add(sourceId); else _muted.Remove(sourceId);
                    _tiledGraph = FilterGraphBuilder.BuildTakeGraph(_take.Sources, _muted);
                    var reason = error.ValueKind == System.Text.Json.JsonValueKind.String
                        ? error.GetString()
                        : "unknown mpv error";
                    throw new InvalidOperationException($"mpv could not update the mute graph: {reason}");
                }
                return;
            }

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
        if (IsTiledLaunch)
        {
            // Tiled-take mode: use the caller's graph and file
            // list. No project/scene/takeDir needed because the
            // caller pre-built the graph from its own data.
            files = _tiledFiles!;
            graph = _tiledGraph;
            // Verify all the source files exist before launching
            // mpv (otherwise the lavfi graph fails mid-start).
            var missing = files.FirstOrDefault(f => !File.Exists(f));
            if (missing is not null)
                throw new FileNotFoundException($"Take media file is missing: {missing}");
        }
        else if (_take is not null)
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
        // Maximum verbosity. The previous --msg-level=all=no
        // hid every error. --msg-level=all=info only showed
        // info+; the actual graph parse errors come at the
        // 'error' or 'verbose' level. all=v captures
        // everything, including demuxer and decoder chatter
        // and the lavfi graph build log. The user only sees
        // this on startup failure; once mpv is up, the
        // chatty per-frame logs are noise the user can
        // ignore in the AppLog.
        psi.ArgumentList.Add("--msg-level=all=v,status=v");
        psi.ArgumentList.Add($"--input-ipc-server={ipcArg}");
        if (graph is not null)
            psi.ArgumentList.Add($"--lavfi-complex={graph}");
        // Log the lavfi graph so it's visible in the user-facing
        // log when the graph is rejected. The user previously
        // only saw "Pipe is broken" with no graph context.
        if (graph is not null)
            AppLog.Current?.WriteLine($"[mpv] --lavfi-complex={graph}");
        psi.ArgumentList.Add(KeepOpen ? "--keep-open=yes" : "--keep-open=no");
        psi.ArgumentList.Add("--osd-level=0");
        psi.ArgumentList.Add(startPaused ? "--pause" : "--no-pause");
        if (EmbedWindowHandle != 0)
        {
            // Force the X11 video output on a Wayland session.
            //
            // --wid is only honoured by mpv's X11 VO. On a Wayland session
            // mpv's auto-detection picks a Wayland output (gpu/gpu-next,
            // dmabuf-wayland, wlshm), where --wid is silently ignored and
            // mpv opens its own independent toplevel window. That window
            // never follows the playback window, so dragging the window
            // left the video behind. NativeVideoHost hands us an X11 window
            // ID because Avalonia runs its X11 backend under XWayland, so
            // pinning mpv to --vo=x11 makes --wid effective again and
            // XWayland parents the surface so it moves with the window.
            //
            // Scoped to Wayland on purpose: on a real X11 session mpv's
            // auto-selected VO is already X11-based, and forcing the x11 VO
            // there would drop us to software scaling for no reason.
            if (OperatingSystem.IsLinux() &&
                LinuxSession.Current == LinuxSession.DisplayServer.Wayland)
                psi.ArgumentList.Add("--vo=x11");
            /* Cast through uint32_t so a "high-bit-set" HWND on Windows
             * doesn't show up as a negative int64 to mpv. mpv's
             * w32_common.c has `if (w32->opts->WinID >= 0)` and treats
             * a negative wid as "don't embed", opening its own window
             * instead. (libmpv docs already require casting to
             * intptr_t; this adds the uint32 step that the Qt
             * reference example uses. Affects ~50% of HWNDs on
             * 64-bit Windows because the kernel hands them out
             * sequentially and many user-mode windows live above
             * 0x80000000.) */
            uint wid32 = (uint)(ulong)EmbedWindowHandle;
            psi.ArgumentList.Add($"--wid={wid32}");
        }
        // Command-line filenames after the first are playlist entries, not
        // simultaneous inputs. lavfi-complex needs one primary file plus
        // external tracks so vidN/aidN all belong to the same playback item.
        foreach (var f in files.Skip(1))
            psi.ArgumentList.Add($"--external-file={f}");
        psi.ArgumentList.Add(files[0]);

        _stderr = new StringBuilder();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Capture stderr into _stderr AND into the user-facing
        // AppLog in real time. _stderr is consumed when the
        // engine times out or fails, but the AppLog path is
        // persistent and visible immediately if the user
        // opens the log folder. This way even if _stderr is
        // somehow cleared before the timeout, the user still
        // sees what mpv said.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _stderr.AppendLine(e.Data);
            AppLog.Current?.WriteLine("[mpv] " + e.Data);
        };
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
            ChildProcessRegistry.Untrack(process);
            throw;
        }

        _process = process;
        // Track so the app can kill a runaway mpv on shutdown.
        // Without this, an mpv stuck waiting for a graph load
        // can keep the IPC pipe open long after the user
        // closed the app, blocking the next session.
        ChildProcessRegistry.Track(process);
        var ipc = _ipc ?? throw new InvalidOperationException("mpv IPC did not connect.");
        ipc.TrafficLog = LogIpcTraffic;
        _fileLoadedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ipc.EventReceived += OnMpvEvent;

        // Wait for mpv to finish loading the file. If the
        // lavfi graph fails (wrong stream labels, missing
        // inputs, etc.), file-loaded never fires. After
        // 10s we treat that as a fatal error and surface
        // mpv's stderr so the user can see why the graph
        // was rejected. The previous "continue anyway" path
        // left mpv in a broken state where every later IPC
        // command returned "Pipe is broken" with no context.
        try
        {
            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // 2 minutes: the user reports mpv always takes
            // "a while" to load. 10s and 30s were too
            // aggressive. The loadCts token also gets
            // cancelled if the caller's CancellationToken
            // fires (e.g. they close the play window), so
            // this doesn't pin mpv for 2 min in the failure
            // case.
            loadCts.CancelAfter(TimeSpan.FromMinutes(2));
            await _fileLoadedTcs.Task.WaitAsync(loadCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            var err = _stderr.ToString().Trim();
            await StopInternalAsync().ConfigureAwait(false);
            // Write mpv's stderr to the user-facing log so it
            // persists across the timeout, even if the user
            // doesn't see the exception message in time.
            if (err.Length > 0)
            {
                AppLog.Current?.WriteLine("[mpv] stderr (captured before timeout):");
                foreach (var line in err.Split('\n'))
                    AppLog.Current?.WriteLine("  " + line.Trim());
            }
            throw new InvalidOperationException(
                $"mpv did not load any streams within 2 minutes. "
                + (graph is not null ? "Likely the lavfi graph was rejected. " : "")
                + (err.Length > 0
                    ? "mpv said: " + err[^Math.Min(err.Length, 2000)..]
                    : "(no stderr output)"),
                ex);
        }
        catch (Exception ex)
        {
            var err = _stderr.ToString().Trim();
            await StopInternalAsync().ConfigureAwait(false);
            if (err.Length > 0)
                AppLog.Current?.WriteLine("[mpv] stderr (captured after load failure): " + err);
            throw new InvalidOperationException(
                $"mpv failed while loading the media. {ex.Message}", ex);
        }

        // Event-driven position tracking (mpv-recommended): mpv pushes the
        // current value immediately, then updates on change. Polling remains
        // as a fallback in PollState. Best-effort: don't fail startup if
        // mpv doesn't respond.
        try
        {
            using var obsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            obsCts.CancelAfter(TimeSpan.FromSeconds(3));
            await ipc.CommandAsync(["observe_property", 1, "time-pos"], obsCts.Token).ConfigureAwait(false);
            await ipc.CommandAsync(["observe_property", 2, "pause"], obsCts.Token).ConfigureAwait(false);
            if (_take is null && _singleFile is not null)
                await ipc.CommandAsync(["observe_property", 3, "duration"], obsCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Startup continues; PollState is the fallback.
        }

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
        // Probe: send a real IPC command. If the graph
        // failed (mpv is about to die or has died), the
        // command throws and we surface the stderr.
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            await _ipc!.CommandAsync(["get_property", "time-pos"], probeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var err = _stderr.ToString().Trim();
            await StopInternalAsync().ConfigureAwait(false);
            if (err.Length > 0)
                throw new InvalidOperationException(
                    $"mpv rejected the startup graph. {ex.GetType().Name}: {ex.Message} mpv said: {err[^Math.Min(err.Length, 1000)..]}",
                    ex);
            throw;
        }
        _loaded = true;
    }

    private void OnMpvEvent(System.Text.Json.JsonElement ev)
    {
        if (!ev.TryGetProperty("event", out var nameProp)) return;
        var name = nameProp.GetString();
        if (name == "file-loaded")
        {
            _fileLoadedTcs?.TrySetResult();
            return;
        }
        if (name == "end-file")
        {
            if (ev.TryGetProperty("reason", out var reasonProp) &&
                reasonProp.GetString() == "error")
            {
                var fileError = ev.TryGetProperty("file_error", out var errorProp)
                    ? errorProp.GetString()
                    : null;
                var message = string.IsNullOrWhiteSpace(fileError)
                    ? "mpv reported a media load error."
                    : $"mpv could not load a media input: {fileError}";
                _fileLoadedTcs?.TrySetException(new InvalidOperationException(message));
                AppLog.Current?.WriteLine("[mpv] " + message);
            }
            PlaybackEnded?.Invoke();
            return;
        }
        if (name == "property-change" &&
            ev.TryGetProperty("id", out var idProp) &&
            idProp.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            var hasData = ev.TryGetProperty("data", out var data) &&
                          data.ValueKind != System.Text.Json.JsonValueKind.Null;
            switch (idProp.GetInt32())
            {
                case 1 when hasData: // time-pos
                    _positionSec = data.GetDouble();
                    break;
                case 2 when hasData: // pause
                    _paused = data.GetBoolean();
                    break;
                case 3 when hasData: // duration (single file)
                    _singleFileDurationSec = data.GetDouble();
                    break;
            }
        }
    }

    private static readonly object IpcLogLock = new();
    private static string? _ipcLogPath;

    private static void LogIpcTraffic(string line)
    {
        try
        {
            lock (IpcLogLock)
            {
                _ipcLogPath ??= Path.Combine(Path.GetTempPath(),
                    $"capturespike-mpv-ipc-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.AppendAllText(_ipcLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never break playback */ }
    }

    /// <summary>Path of the current session's mpv IPC traffic log (for diagnostics).</summary>
    public static string? IpcLogPath => _ipcLogPath;

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
        {
            await KillProcessAsync(process).ConfigureAwait(false);
            // Process is dead; remove from the registry so
            // the app's shutdown handler doesn't try to kill
            // it again (and so the list doesn't grow over the
            // session as the user opens/closes the player).
            ChildProcessRegistry.Untrack(process);
        }
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
