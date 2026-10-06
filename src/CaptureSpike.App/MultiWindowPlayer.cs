using Avalonia.Controls;
using CaptureSpike.Core;
using CaptureSpike.Core.Playback;

namespace CaptureSpike.App;

/// <summary>
/// A window hosting one take source's video via an embedded mpv.
/// The native child handle is exposed once created.
/// </summary>
public sealed class SourcePlaybackWindow : Window
{
    private readonly MpvVideoHost _host = new();
    private readonly TaskCompletionSource<nint> _handleTcs = new();

    public SourcePlaybackWindow(string title)
    {
        Title = title;
        Width = 960;
        Height = 540;
        Content = _host;
        _host.HandleCreated += h => _handleTcs.TrySetResult(h);
        _host.HandleDestroyed += () => _handleTcs.TrySetCanceled();
    }

    /// <summary>Completes with the native child handle mpv should render into.</summary>
    public Task<nint> WaitForHandleAsync() => _handleTcs.Task;
}

/// <summary>
/// Plays a take's sources each in its own window (video) or headless (audio),
/// synchronized to the take timeline. One shared transport (play/pause/seek)
/// drives every source; each source's take.json offset is applied on seek.
/// Shorter files hold their last frame (mpv --keep-open) while longer ones finish.
/// </summary>
public sealed class MultiWindowPlayer : IPlayer
{
    private sealed class SourcePlayer
    {
        public required string SourceId;
        public required MpvPlaybackEngine Engine;
        public SourcePlaybackWindow? Window;
        public double OffsetSec;
        public bool Included = true;
        public bool Muted;
    }

    private readonly List<SourcePlayer> _players = new();
    private readonly double _takeDurationSec;
    private bool _disposed;

    /// <summary>Raised when a source is excluded (e.g. its window was closed).</summary>
    public event Action<string>? SourceExcluded;

    public MultiWindowPlayer(double takeDurationSec)
    {
        _takeDurationSec = takeDurationSec;
    }

    /// <summary>
    /// Adds a source. Video sources get an embedded mpv in their own window;
    /// audio sources play headless. Starts paused; call <see cref="PlayAsync"/>
    /// once all sources are added.
    /// </summary>
    public async Task AddSourceAsync(
        TakeSource source, string label, string filePath,
        Func<TakeSource, string, SourcePlaybackWindow> createWindow,
        CancellationToken ct = default)
    {
        var isVideo = source.Kind.IsVideo();
        SourcePlaybackWindow? window = null;
        nint handle = 0;
        if (isVideo)
        {
            window = createWindow(source, label);
            window.Show();
            try
            {
                // The native child is created during layout; give it a moment.
                // If embedding fails, fall back to mpv's own window (handle 0).
                handle = await window.WaitForHandleAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                handle = 0;
            }
        }

        var engine = new MpvPlaybackEngine
        {
            EmbedWindowHandle = handle,
            KeepOpen = true,
        };
        try
        {
            await engine.LoadSingleFileAsync(filePath, startPaused: true, ct).ConfigureAwait(false);
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            window?.Close();
            throw;
        }

        var player = new SourcePlayer
        {
            SourceId = source.Id,
            Engine = engine,
            Window = window,
            OffsetSec = source.OffsetMs / 1000.0,
        };
        if (window is not null)
        {
            // If the user closes a source window, exclude it from playback.
            window.Closed += async (_, _) =>
            {
                player.Included = false;
                try { await engine.PauseAsync().ConfigureAwait(false); } catch { }
                SourceExcluded?.Invoke(player.SourceId);
            };
        }
        _players.Add(player);
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

    /// <summary>Includes or excludes a source from playback (hides/shows its window).</summary>
    public async Task SetSourceIncludedAsync(string sourceId, bool included, CancellationToken ct = default)
    {
        var p = _players.FirstOrDefault(x => x.SourceId == sourceId);
        if (p is null || p.Included == included) return;
        p.Included = included;
        if (!included)
        {
            await p.Engine.PauseAsync(ct).ConfigureAwait(false);
            p.Window?.Hide();
        }
        else
        {
            // Rejoin at the current take-time; resume if the group is playing.
            var wasPlaying = IsPlaying;
            await p.Engine.SeekAsync(
                TimeSpan.FromSeconds(Math.Max(0, Position.TotalSeconds - p.OffsetSec)), ct)
                .ConfigureAwait(false);
            p.Window?.Show();
            if (wasPlaying) await p.Engine.PlayAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task SetSourceMutedAsync(string sourceId, bool muted, CancellationToken ct = default)
    {
        var p = _players.FirstOrDefault(x => x.SourceId == sourceId);
        if (p is null) return;
        p.Muted = muted;
        await p.Engine.SetMutedAsync(muted, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in _players)
        {
            try { await p.Engine.DisposeAsync().ConfigureAwait(false); } catch { }
            try { p.Window?.Close(); } catch { }
        }
        _players.Clear();
    }
}
