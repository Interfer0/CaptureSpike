using CaptureSpike.Core;
using CaptureSpike.Core.Playback;

namespace CaptureSpike.App;

/// <summary>
/// Plays a take's sources in sync. Internally uses a single
/// mpv instance with a <c>--lavfi-complex</c> graph that tiles
/// the video sources and mixes the audio. One shared transport
/// (play/pause/seek) drives everything, and a single audio
/// clock keeps every source in lock-step — no per-process
/// drift like the old per-source mpv design had.
///
/// For backward compatibility, the caller can still call
/// <see cref="AddSourceAsync"/> once per source with a per-tile
/// embed handle. We only use one of those handles (the first
/// non-zero one) for the single mpv output; the rest are
/// ignored.
///
/// Offset model (matches FilterGraphBuilder.BuildTakeGraph):
/// each source's take.json EffectiveOffsetMs says how late its
/// file-time 0 sits on the take timeline. Offsets are
/// normalized by subtracting the smallest, so at take-time T a
/// source is at file-time (T - relativeOffset). A source with
/// a larger offset shows earlier file content at the same
/// take-time.
///
/// Per-source mute and include/exclude are handled by
/// rebuilding the filter graph with the muted set
/// (see MpvPlaybackEngine.SetTrackMutedAsync).
/// </summary>
public sealed class TiledTakePlayer : IPlayer
{
    private sealed class SourceInfo
    {
        public required string SourceId;
        public required TakeSource TakeSource;
        public required string FilePath;
    }

    private readonly List<SourceInfo> _sources = new();
    private readonly double _takeDurationSec;
    private MpvPlaybackEngine? _engine;
    private nint _embedHandle;
    private bool _disposed;

    public TiledTakePlayer(double takeDurationSec)
    {
        _takeDurationSec = takeDurationSec;
    }

    /// <summary>
    /// Adds a source. <paramref name="embedHandle"/> is the
    /// native child HWND the single mpv instance should render
    /// into (0 = mpv's own window, or headless for audio-only
    /// takes). The actual mpv launch is deferred until the
    /// caller invokes <see cref="PlayAlignedAsync"/> or
    /// <see cref="PlayAsync"/>.
    /// </summary>
    public Task AddSourceAsync(
        TakeSource source, string filePath, nint embedHandle,
        CancellationToken ct = default)
    {
        _sources.Add(new SourceInfo
        {
            SourceId = source.Id,
            TakeSource = source,
            FilePath = filePath,
        });
        // Single mpv renders to one window. Use the first
        // non-zero handle we see. If everything is 0
        // (audio-only take), mpv runs without a window.
        if (_embedHandle == 0 && embedHandle != 0) _embedHandle = embedHandle;
        return Task.CompletedTask;
    }

    // Per-source mute state. Populated only by
    // SetSourceIncludedAsync / SetSourceMutedAsync. The
    // initial "everything is unmuted" semantics means
    // _muted starts empty — sources are unmuted by default.
    private readonly HashSet<string> _muted = new();

    // Per-source include state. Mirrors the per-track toggle
    // in the player panel: when a source is "not included",
    // its audio is muted in the graph (and its video, if any,
    // is also dropped from the tiled layout). Kept in sync
    // with _muted for query convenience.
    private readonly HashSet<string> _included = new();

    public TimeSpan Position =>
        _engine is null ? TimeSpan.Zero : _engine.Position;

    public TimeSpan Duration =>
        _engine is null
            ? TimeSpan.FromSeconds(Math.Max(0, _takeDurationSec))
            : _engine.Duration;

    public bool IsPlaying => _engine is { IsPlaying: true };

    public async Task PlayAsync(CancellationToken ct = default)
    {
        if (_engine is null) await BuildEngineAsync(ct).ConfigureAwait(false);
        if (_engine is not null) await _engine.PlayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeks each source to its offset-aligned start position,
    /// then plays. Starts at take-time = largest relative
    /// offset so every source has a valid file position and
    /// all show the same take-time. (No-op in single-mpv
    /// mode — mpv's graph already aligns the offsets via
    /// adelay.)
    /// </summary>
    public async Task PlayAlignedAsync(CancellationToken ct = default)
    {
        if (_engine is null) await BuildEngineAsync(ct).ConfigureAwait(false);
        if (_engine is not null) await _engine.PlayAsync(ct).ConfigureAwait(false);
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        if (_engine is null) return;
        await _engine.PauseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Seeks every included source to take-time minus its relative offset.</summary>
    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (_engine is null) await BuildEngineAsync(ct).ConfigureAwait(false);
        if (_engine is not null) await _engine.SeekAsync(position, ct).ConfigureAwait(false);
    }

    /// <summary>Includes or excludes a source from playback (mutes in the graph).</summary>
    public async Task SetSourceIncludedAsync(string sourceId, bool included, CancellationToken ct = default)
    {
        if (included)
        {
            _included.Add(sourceId);
            _muted.Remove(sourceId);
        }
        else
        {
            _included.Remove(sourceId);
            _muted.Add(sourceId);
        }
        if (_engine is null) return;
        // MpvPlaybackEngine's mute handling rebuilds the graph
        // with the new muted set. We model "not included" as
        // "muted" for the graph.
        await _engine.SetTrackMutedAsync(sourceId, !included, ct).ConfigureAwait(false);
    }

    /// <summary>Updates a source's offset live (e.g., after a manual offset edit).</summary>
    public void UpdateSourceOffset(string sourceId, double offsetMs)
    {
        // The graph is already built with the recorded offset.
        // Live offset updates are not supported in the
        // single-mpv design (would require a graph rebuild
        // with the new adelay). The UI should mark the take
        // as needing a re-play to pick up manual offset
        // changes. (For now: no-op.)
    }

    public async Task SetSourceMutedAsync(string sourceId, bool muted, CancellationToken ct = default)
    {
        if (muted) _muted.Add(sourceId); else _muted.Remove(sourceId);
        if (_engine is null) return;
        await _engine.SetTrackMutedAsync(sourceId, muted, ct).ConfigureAwait(false);
    }

    private async Task BuildEngineAsync(CancellationToken ct)
    {
        if (_engine is not null) return;
        if (_sources.Count == 0)
            throw new InvalidOperationException("No sources to play.");
        var engine = new MpvPlaybackEngine
        {
            EmbedWindowHandle = _embedHandle,
            KeepOpen = true,
        };
        // Build the muted set. The semantics: a source is
        // muted only if it has been EXPLICITLY toggled off by
        // the user (via SetSourceIncludedAsync). On first
        // call, nothing has been toggled, so nothing is muted.
        // We track explicit-off state in _muted (a HashSet
        // populated only by SetSourceIncludedAsync). _included
        // is just the inverse cache used for queries; do not
        // build muted from "not in _included" — that mutes
        // everything on first call.
        var muted = new HashSet<string>(_muted);
        var graph = FilterGraphBuilder.BuildTakeGraph(
            _sources.Select(s => s.TakeSource).ToList(),
            muted);
        // Pass files to mpv in a deterministic order so the
        // graph's [i:v] and [N+i:a] labels match mpv's
        // file-position labels. Videos first, then audios.
        var videoSources = _sources.Where(s => s.TakeSource.Kind.IsVideo()).ToList();
        var audioSources = _sources.Where(s => s.TakeSource.Kind.IsAudio()).ToList();
        var filePaths = videoSources.Select(s => s.FilePath)
            .Concat(audioSources.Select(s => s.FilePath))
            .ToList();
        var takeSources = videoSources.Select(s => s.TakeSource)
            .Concat(audioSources.Select(s => s.TakeSource))
            .ToList();
        await engine.LoadAsync(filePaths, takeSources, graph, _takeDurationSec, muted, ct)
            .ConfigureAwait(false);
        _engine = engine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_engine is not null)
        {
            try { await _engine.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _sources.Clear();
    }
}
