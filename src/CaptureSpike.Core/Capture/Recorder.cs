namespace CaptureSpike.Core.Capture;

/// <summary>
/// Starts one capturer per enabled scene source against a shared t0.
/// All capturers start in parallel (not one after another), so a 4-source
/// scene takes ~10s max instead of up to 40s. Fail-fast: if any source fails
/// to start, the ones already running are torn down and the error surfaces —
/// no half-recorded takes.
/// </summary>
public sealed class Recorder : IRecorder
{
    private readonly ProjectStore _store;
    private readonly string? _ffmpegOverride;

    public Recorder(ProjectStore store, string? ffmpegOverride = null)
    {
        _store = store;
        _ffmpegOverride = ffmpegOverride;
    }

    public async Task<IRecordingSession> StartAsync(Project project, Scene scene, string takeDir,
        IProgress<TimeSpan>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(takeDir);
        var ffmpeg = ToolPaths.Resolve("ffmpeg", _ffmpegOverride);
        var enabled = scene.Sources.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
            throw new InvalidOperationException($"Scene '{scene.Title}' has no enabled sources.");

        var t0 = DateTimeOffset.UtcNow;

        // Create all capturers up front so a missing source fails before
        // any process is launched.
        var takeBaseName = Path.GetFileName(takeDir); // "Take 05"
        // Look up source definitions on the project first (per-project
        // picks like badge/label live there), fall back to the global
        // registry for kinds the project doesn't have yet.
        var registry = GlobalPalette.Load().Sources;
        var capturers = enabled.Select(sceneSource =>
        {
            var def = project.Sources.FirstOrDefault(p => p.Id == sceneSource.SourceId)
                ?? registry.FirstOrDefault(p => p.Id == sceneSource.SourceId)
                ?? throw new InvalidOperationException(
                    $"Scene '{scene.Title}' references a source that no longer exists. " +
                    "Remove it from the scene and try again.");
            // MKV, not MP4: a killed ffmpeg/crash/power cut still leaves a
            // playable file (no moov atom needed at the end).
            var ext = def.Kind.IsVideo() ? "mkv" : "wav";
            // Resolve-friendly: Project-Scene-Take 05-Badge-Label.ext
            var baseName = string.Join("-", new[]
            {
                Naming.SanitizeFileName(project.Name),
                Naming.SanitizeFileName(scene.Title),
                takeBaseName,
                Naming.SanitizeFileName(def.EffectiveBadge()),
                Naming.SanitizeFileName(def.Label),
            });
            var path = Path.Combine(takeDir, baseName + "." + ext);
            for (var i = 2; File.Exists(path); i++)
                path = Path.Combine(takeDir, $"{baseName} {i}.{ext}");
            return CaptureFactory.Create(project, def, path, ffmpeg);
        }).ToList();

        try
        {
            // Parallel start: every source warms up at once.
            var startTasks = capturers.Select(c => c.StartAsync(t0, ct)).ToList();
            try
            {
                await Task.WhenAll(startTasks).ConfigureAwait(false);
            }
            catch
            {
                // Observe the losers' exceptions so they don't go unobserved.
                foreach (var t in startTasks.Where(t => !t.IsCompletedSuccessfully))
                    _ = t.ContinueWith(failed => _ = failed.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
        }
        catch
        {
            foreach (var c in capturers)
            {
                try { await c.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
        return new RecordingSession(_store, scene, takeDir, t0, capturers, progress);
    }
}

public sealed class RecordingSession : IRecordingSession
{
    private readonly ProjectStore _store;
    private readonly Scene _scene;
    private readonly string _takeDir;
    private readonly DateTimeOffset _t0;
    private readonly List<ISourceCapturer> _capturers;
    private readonly IProgress<TimeSpan>? _progress;
    private readonly Timer _timer;
    private bool _stopped;

    public DateTimeOffset StartedAt => _t0;
    public IReadOnlyList<string> ActiveSourceIds => _capturers.Select(c => c.SourceId).ToList();

    public RecordingSession(ProjectStore store, Scene scene, string takeDir, DateTimeOffset t0,
        List<ISourceCapturer> capturers, IProgress<TimeSpan>? progress)
    {
        _store = store;
        _scene = scene;
        _takeDir = takeDir;
        _t0 = t0;
        _capturers = capturers;
        _progress = progress;
        _timer = new Timer(_ => _progress?.Report(DateTimeOffset.UtcNow - _t0),
            null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public async Task<Take> StopAsync(CancellationToken ct = default)
    {
        if (_stopped) throw new InvalidOperationException("This recording session is already stopped.");
        _stopped = true;
        try { await _timer.DisposeAsync().ConfigureAwait(false); } catch { }

        var endedAt = DateTimeOffset.UtcNow;
        foreach (var c in _capturers)
        {
            try { await c.StopAsync(ct).ConfigureAwait(false); }
            catch { /* keep stopping the rest; a stuck source must not lose the take */ }
        }

        var take = new Take
        {
            Id = Path.GetFileName(_takeDir),
            SceneId = _scene.Id,
            Number = ProjectStore.TakeNumberFromFolder(Path.GetFileName(_takeDir))
                ?? _store.LoadTakes(_scene.FolderName).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1,
            StartedAt = _t0,
            DurationSeconds = (endedAt - _t0).TotalSeconds,
            Sources = _capturers
                .Where(c => File.Exists(c.OutputFile))
                .Select(c => new TakeSource
                {
                    Id = c.SourceId,
                    Kind = c.Kind,
                    File = Path.GetFileName(c.OutputFile),
                    OffsetMs = c.FirstDataAt.HasValue
                        ? (c.FirstDataAt.Value - _t0).TotalMilliseconds : 0,
                })
                .ToList(),
        };
        _store.SaveTake(_takeDir, take);

        foreach (var c in _capturers)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        return take;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _timer.DisposeAsync().ConfigureAwait(false); } catch { }
        if (_stopped) return;
        // Abandoned without Stop: halt capture but don't write a take.
        foreach (var c in _capturers)
        {
            try { await c.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await c.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }
}
