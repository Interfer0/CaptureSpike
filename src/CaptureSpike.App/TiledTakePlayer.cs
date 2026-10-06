using CaptureSpike.Core;
using CaptureSpike.Core.Playback;

namespace CaptureSpike.App;

/// <summary>
/// Plays a take's sources in sync: video sources render into a tiled window
/// (handles provided by the caller), audio sources play headless.
/// One shared transport (play/pause/seek) drives every source; each source's
/// take.json offset is applied on seek. Shorter files hold their last frame
/// (mpv --keep-open) while longer ones finish.
/// </summary>
public sealed class TiledTakePlayer : IPlayer
{
    private sealed class SourcePlayer
    {
        public required string SourceId;
        public required MpvPlaybackEngine Engine;
        public double OffsetSec;
        public bool Included = true;
    }

    private readonly List<SourcePlayer> _players = new();
    private readonly double _takeDurationSec;
    private bool _disposed;

    public TiledTakePlayer(double takeDurationSec)
    {
        _takeDurationSec = takeDurationSec;
    }

    /// <summary>
    /// Adds a source. <paramref name="embedHandle"/> is the native child mpv
    /// should render into (0 = mpv's own window, or headless for audio).
    /// Starts paused; call <see cref="PlayAsync"/> once all sources are added.
    /// </summary>
    public async Task AddSourceAsync(
        TakeSource source, string filePath, nint embedHandle,
        CancellationToken ct = default)
    {
        var engine = new MpvPlaybackEngine
        {
            EmbedWindowHandle = embedHandle,
            KeepOpen = true,
        };
        try
        {
            await engine.LoadSingleFileAsync(filePath, startPaused: true, ct).ConfigureAwait(false);
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _players.Add(new SourcePlayer
        {
            SourceId = source.Id,
            Engine = engine,
            OffsetSec = source.OffsetMs / 1000.0,
        });
    }

    public TimeSpan Position
    {
        get
        {
            // Take-time = furthest source media time + its offset.
            var max = 0.0;
            foreach (var p in _players.Where(p => p.Included))
                max = Math.Max(max, p.Engine.Position.TotalSeconds + p.OffsetSec);
            return TimeSpan.FromSeconds(max);
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, _takeDurationSec));

    public bool IsPlaying => _players.Any(p => p.Included && p.Engine.IsPlaying);

    public async Task PlayAsync(CancellationToken ct = default)
    {
        var tasks = _players.Where(p => p.Included).Select(p => p.Engine.PlayAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        var tasks = _players.Where(p => p.Included).Select(p => p.Engine.PauseAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Seeks every included source to its offset-adjusted media time.</summary>
    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        var tasks = _players.Where(p => p.Included).Select(p =>
            p.Engine.SeekAsync(
                TimeSpan.FromSeconds(Math.Max(0, position.TotalSeconds - p.OffsetSec)), ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Includes or excludes a source from playback.</summary>
    public async Task SetSourceIncludedAsync(string sourceId, bool included, CancellationToken ct = default)
    {
        var p = _players.FirstOrDefault(x => x.SourceId == sourceId);
        if (p is null || p.Included == included) return;
        p.Included = included;
        if (!included)
        {
            await p.Engine.PauseAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Rejoin at the current take-time; resume if the group is playing.
            var wasPlaying = IsPlaying;
            await p.Engine.SeekAsync(
                TimeSpan.FromSeconds(Math.Max(0, Position.TotalSeconds - p.OffsetSec)), ct)
                .ConfigureAwait(false);
            if (wasPlaying) await p.Engine.PlayAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task SetSourceMutedAsync(string sourceId, bool muted, CancellationToken ct = default)
    {
        var p = _players.FirstOrDefault(x => x.SourceId == sourceId);
        if (p is null) return;
        await p.Engine.SetMutedAsync(muted, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in _players)
        {
            try { await p.Engine.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _players.Clear();
    }
}
