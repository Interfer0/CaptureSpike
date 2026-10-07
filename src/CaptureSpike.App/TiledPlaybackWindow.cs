using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CaptureSpike.App;

/// <summary>Info for one tile in <see cref="TiledPlaybackWindow"/>.</summary>
public sealed record TileSourceInfo(
    string SourceId,
    string Label,
    string Badge,
    string FileName,
    bool IsVideo);

/// <summary>
/// One window showing all of a take's sources in a tile grid.
/// Video tiles host an embedded mpv; audio tiles show the filename and
/// toggle mute on click.
///
/// Theme: all brushes come from the application theme dictionaries
/// (see App.axaml ThemeKeys). The window reads them at construction
/// and again whenever the theme changes, via RefreshThemeBrushes().
/// </summary>
public sealed class TiledPlaybackWindow : Window
{
    /// <summary>
    /// Resolves a theme brush by key. Returns a sensible fallback if the
    /// key is missing (e.g. running in the XAML designer without the
    /// full resource tree). Falls back to a black brush since the tiles
    /// are always over video content where black is rarely wrong.
    /// </summary>
    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush
        ?? new SolidColorBrush(Color.FromRgb(0, 0, 0));

    private static ISolidColorBrush SolidBrush(string key, Color fallback) =>
        Avalonia.Application.Current?.FindResource(key) as ISolidColorBrush
        ?? new SolidColorBrush(fallback);
    private sealed class Tile
    {
        public required string SourceId;
        public required Border Border;
        public required TaskCompletionSource<nint> HandleTcs;
        public TextBlock? MuteGlyph;
        public TextBlock? MuteLabel;
        public Panel? AudioPanel;
    }

    private readonly List<Tile> _tiles = new();
    private readonly Action<string> _onAudioTileClicked;
    private TextBlock? _loadingText;

    public TiledPlaybackWindow(
        string title,
        IReadOnlyList<TileSourceInfo> sources,
        Action<string> onAudioTileClicked)
    {
        _onAudioTileClicked = onAudioTileClicked;
        Title = title;
        Width = 1280;
        Height = 720;

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

        var cols = (int)Math.Ceiling(Math.Sqrt(sources.Count));
        var rows = (int)Math.Ceiling(sources.Count / (double)cols);

        var grid = new Grid();
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brush(ThemeKeys.TileBorder),
                Background = Brush(ThemeKeys.MutedTile),
            };

            var tcs = new TaskCompletionSource<nint>();
            TextBlock? muteGlyph = null;

            if (src.IsVideo)
            {
                var host = new MpvVideoHost();
                host.HandleCreated += h => tcs.TrySetResult(h);
                host.HandleDestroyed += () => tcs.TrySetCanceled();

                var caption = new TextBlock
                {
                    Text = $"{src.Badge} · {src.Label}",
                    FontSize = 13,
                    Padding = new Thickness(6, 2),
                    // Caption sits on top of arbitrary video frames, so it
                    // keeps a fixed semi-transparent dark scrim and white
                    // text regardless of theme.
                    Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };

                var panel = new Panel();
                panel.Children.Add(host);
                panel.Children.Add(caption);
                border.Child = panel;
                // Video tile: TCS completes when the native handle is created.
                _tiles.Add(new Tile
                {
                    SourceId = src.SourceId,
                    Border = border,
                    HandleTcs = tcs,
                });
            }
            else
            {
                // Audio tile: filename + mute state, click to toggle mute.
                muteGlyph = new TextBlock
                {
                    Text = "🔊",
                    FontSize = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var labelBlock = new TextBlock
                {
                    Text = $"{src.Badge} · {src.Label}",
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                    Foreground = SolidBrush(ThemeKeys.BadgeForeground, Color.FromRgb(240, 240, 240)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var fileBlock = new TextBlock
                {
                    Text = src.FileName,
                    FontSize = 13,
                    FontFamily = new FontFamily("Monospace"),
                    // Same as labels, slightly faded via Opacity rather than alpha.
                    Foreground = SolidBrush(ThemeKeys.BadgeForeground, Color.FromRgb(160, 160, 160)),
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var muteLabel = new TextBlock
                {
                    Text = "",
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.Red,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var hint = new TextBlock
                {
                    Text = "click to mute",
                    FontSize = 11,
                    Foreground = SolidBrush(ThemeKeys.BadgeForeground, Color.FromRgb(160, 160, 160)),
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var stack = new StackPanel
                {
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                stack.Children.Add(muteGlyph);
                stack.Children.Add(labelBlock);
                stack.Children.Add(fileBlock);
                stack.Children.Add(muteLabel);
                stack.Children.Add(hint);
                var audioPanel = new Panel { Background = Brush(ThemeKeys.MutedTile) };
                audioPanel.Children.Add(stack);
                border.Child = audioPanel;
                border.Cursor = new Cursor(StandardCursorType.Hand);

                var capturedId = src.SourceId;
                border.PointerPressed += (_, _) => _onAudioTileClicked(capturedId);
                tcs.TrySetResult(nint.Zero);

                _tiles.Add(new Tile
                {
                    SourceId = src.SourceId,
                    Border = border,
                    HandleTcs = tcs,
                    MuteGlyph = muteGlyph,
                    MuteLabel = muteLabel,
                    AudioPanel = audioPanel,
                });
            }

            Grid.SetRow(border, i / cols);
            Grid.SetColumn(border, i % cols);
            grid.Children.Add(border);
        }

        // Loading overlay (hidden when SetLoadingComplete is called). Fixed dark
        // scrim with white text — same rationale as the video-tile caption
        // (always visible, regardless of theme).
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
        var root = new Panel();
        root.Children.Add(grid);
        root.Children.Add(_loadingText);
        Content = root;
    }

    /// <summary>Handles for each tile's native child, in tile order. 0 = no video / embedding failed.</summary>
    public async Task<Dictionary<string, nint>> WaitForHandlesAsync(TimeSpan timeout)
    {
        var result = new Dictionary<string, nint>();
        foreach (var tile in _tiles)
        {
            nint handle = 0;
            try
            {
                handle = await tile.HandleTcs.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (Exception) { /* embedding failed; mpv uses its own window */ }
            result[tile.SourceId] = handle;
        }
        return result;
    }

    /// <summary>Shows or hides a tile (used by the include/exclude toggle).</summary>
    public void SetTileVisible(string sourceId, bool visible)
    {
        var tile = _tiles.FirstOrDefault(t => t.SourceId == sourceId);
        if (tile is not null) tile.Border.IsVisible = visible;
    }

    /// <summary>Updates the mute visual on an audio tile (glyph + red MUTED banner).</summary>
    public void SetTileMuted(string sourceId, bool muted)
    {
        var tile = _tiles.FirstOrDefault(t => t.SourceId == sourceId);
        if (tile is null) return;
        if (tile.MuteGlyph is not null)
            tile.MuteGlyph.Text = muted ? "🔇" : "🔊";
        if (tile.MuteLabel is not null)
            tile.MuteLabel.Text = muted ? "MUTED" : "";
        if (tile.AudioPanel is not null)
            tile.AudioPanel.Background = muted
                ? Brush(ThemeKeys.MutedTileMuted)
                : Brush(ThemeKeys.MutedTile);
        tile.Border.BorderBrush = muted
            ? Brush(ThemeKeys.TileBorderMuted)
            : Brush(ThemeKeys.TileBorder);
    }

    /// <summary>Hides the loading overlay once all sources are playing.</summary>
    public void SetLoadingComplete()
    {
        if (_loadingText is not null)
            _loadingText.IsVisible = false;
    }
}
