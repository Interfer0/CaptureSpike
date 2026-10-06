namespace CaptureSpike.Core.Playback;

/// <summary>
/// A take player: either a single mpv engine (composite take or single file)
/// or a synchronized group of per-source engines (multi-window).
/// </summary>
public interface IPlayer : IAsyncDisposable
{
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }
    Task PlayAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);
}
