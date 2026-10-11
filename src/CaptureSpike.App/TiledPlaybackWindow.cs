using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CaptureSpike.App;

/// <summary>Info for one source in <see cref="TiledPlaybackWindow"/>.</summary>
public sealed record TileSourceInfo(
    string SourceId,
    string Label,
    string Badge,
    string FileName,
    bool IsVideo);

/// <summary>
/// One window for playing a take. Internally the single-mpv
/// player renders every source's video and audio into one
/// lavfi graph (xstack + amix) and outputs to a single
/// <see cref="MpvVideoHost"/> that fills the content area.
/// That gives the user one synchronized video surface
/// showing every source tiled, with one audio stream mixed
/// from every audio source — no per-process drift.
///
/// A track strip at the bottom shows each source's
/// label/badge with a click-to-mute button so the user can
/// still mute individual tracks.
///
/// Theme: brushes come from the application theme dictionaries
/// (see App.axaml ThemeKeys).
/// </summary>
public sealed class TiledPlaybackWindow : Window
{
    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush
        ?? new SolidColorBrush(Color.FromRgb(0, 0, 0));

    private static ISolidColorBrush SolidBrush(string key, Color fallback) =>
        Avalonia.Application.Current?.FindResource(key) as ISolidColorBrush
        ?? new SolidColorBrush(fallback);

    private readonly List<TileSourceInfo> _sources;
    private readonly Action<string> _onTrackClicked;
    private readonly MpvVideoHost _videoHost;
    private readonly TaskCompletionSource<nint> _handleTcs = new();
    private TextBlock? _loadingText;
    private readonly Dictionary<string, Border> _trackBorders = new();
    private PlayerTransportBar? _transport;

    public TiledPlaybackWindow(
        string title,
        IReadOnlyList<TileSourceInfo> sources,
        Action<string> onTrackClicked,
        ITransportHost? transport = null)
    {
        _sources = sources.ToList();
        _onTrackClicked = onTrackClicked;
        Title = title;
        // Sized to match a 2-cell xstack cell (1280x720) so the
        // single mpv output shows each 640x360 cell clearly.
        // When there are more sources xstack auto-extends the
        // output canvas; the window keeps its base size and
        // mpv scales the tiled composition to fit.
        Width = 1280;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = true;

        if (sources.Count == 0)
        {
            Content = new TextBlock
            {
                Text = "No sources in this take.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }

        // Single big mpv host: the entire tiled composition
        // from the lavfi graph renders here. mvp + xstack tile
        // every source into this surface, so we get one
        // synchronized view of everything.
        _videoHost = new MpvVideoHost();
        _videoHost.HandleCreated += h => _handleTcs.TrySetResult(h);
        _videoHost.HandleDestroyed += () => _handleTcs.TrySetCanceled();

        // Track strip below the video: one clickable row per
        // source, shows badge + label, click to mute.
        var tracksPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4),
        };
        foreach (var src in sources)
        {
            var badge = src.Badge;
            var label = src.Label;
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brush(ThemeKeys.TileBorder),
                Background = Brush(ThemeKeys.MutedTile),
                Padding = new Thickness(8, 4),
                CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(badge) ? label : $"{badge} · {label}",
                    FontSize = 13,
                    Foreground = SolidBrush(ThemeKeys.BadgeForeground, Color.FromRgb(240, 240, 240)),
                },
            };
            var capturedId = src.SourceId;
            border.PointerPressed += (_, _) => _onTrackClicked(capturedId);
            _trackBorders[src.SourceId] = border;
            tracksPanel.Children.Add(border);
        }

        var videoPanel = new Panel();
        videoPanel.Children.Add(_videoHost);

        // Loading overlay.
        _loadingText = new TextBlock
        {
            Text = "⏳ Loading sources...",
            FontSize = 18,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            Padding = new Thickness(20, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        videoPanel.Children.Add(_loadingText);

        var dock = new DockPanel();
        if (transport is not null)
        {
            _transport = new PlayerTransportBar();
            _transport.Attach(transport);
            DockPanel.SetDock(_transport, Dock.Bottom);
            dock.Children.Add(_transport);
        }
        var tracksRow = new Border
        {
            Background = Brush(ThemeKeys.MutedTile),
            Padding = new Thickness(8, 4),
            Child = tracksPanel,
        };
        DockPanel.SetDock(tracksRow, Dock.Bottom);
        dock.Children.Add(tracksRow);
        dock.Children.Add(videoPanel);
        Content = dock;
    }

    /// <summary>
    /// Returns a single dictionary whose only key is the first
    /// video source's id and whose value is the single mpv
    /// host's native handle. Audio sources map to handle 0.
    /// The single-mpv engine uses the first non-zero handle
    /// for its embed target; the rest are ignored. Kept as a
    /// dictionary so the caller doesn't need to know about
    /// the single-mpv design.
    /// </summary>
    public async Task<Dictionary<string, nint>> WaitForHandlesAsync(TimeSpan timeout)
    {
        nint handle = 0;
        try
        {
            handle = await _handleTcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception) { /* embedding failed; mpv uses its own window */ }
        var result = new Dictionary<string, nint>();
        foreach (var src in _sources)
        {
            // Only the first video source's id maps to the
            // actual embed handle. Everything else is 0
            // (audio / ignored). The single-mpv engine
            // internally uses the first non-zero value.
            result[src.SourceId] = src.IsVideo && handle != 0 && !result.Any() ? handle : nint.Zero;
        }
        return result;
    }

    /// <summary>Highlights / de-highlights a track row when it's muted.</summary>
    public void SetTileMuted(string sourceId, bool muted)
    {
        if (!_trackBorders.TryGetValue(sourceId, out var border)) return;
        border.BorderBrush = muted
            ? Brush(ThemeKeys.TileBorderMuted)
            : Brush(ThemeKeys.TileBorder);
        border.Opacity = muted ? 0.5 : 1.0;
    }

    /// <summary>Compatibility shim with the old per-tile window API. No-op now.</summary>
    public void SetTileVisible(string sourceId, bool visible)
    {
        // The single-mpv design doesn't have per-source tiles
        // — including/excluding a source rebuilds the filter
        // graph instead. The track strip stays visible so the
        // user can re-include a source.
    }

    /// <summary>Hides the loading overlay once mpv is up and playing.</summary>
    public void SetLoadingComplete()
    {
        if (_loadingText is not null) _loadingText.IsVisible = false;
    }
}
