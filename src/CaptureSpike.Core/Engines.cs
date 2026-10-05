namespace CaptureSpike.Core;

/// <summary>Live recording session. Dispose/Stop finalizes the take.</summary>
public interface IRecordingSession : IAsyncDisposable
{
    DateTimeOffset StartedAt { get; }
    IReadOnlyList<string> ActiveSourceIds { get; }
    /// <summary>Stops capture and writes take.json into the take folder.</summary>
    Task<Take> StopAsync(CancellationToken ct = default);
}

/// <summary>Starts a recording session for a scene. UI talks only to this.</summary>
public interface IRecorder
{
    Task<IRecordingSession> StartAsync(
        Project project, Scene scene, string takeDir,
        IProgress<TimeSpan>? progress = null, CancellationToken ct = default);
}

/// <summary>Plays one take back with per-track mute/hide. UI talks only to this.</summary>
public interface IPlaybackEngine : IAsyncDisposable
{
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }

    Task LoadAsync(string takeDir, Take take, CancellationToken ct = default);
    Task PlayAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);
    Task SetTrackMutedAsync(string sourceId, bool muted, CancellationToken ct = default);
    /// <summary>Native window handle to embed video into. Set before Load.</summary>
    nint EmbedWindowHandle { get; set; }
}

/// <summary>
/// Renders each scene's favorite take (composited per scene layout) and
/// concatenates them in scene order into one MP4, with a chapter per scene.
/// </summary>
public interface IExporter
{
    Task ExportAsync(
        ProjectStore store, string outputPath,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default);
}

public sealed record ExportProgress(string Stage, double Fraction);
