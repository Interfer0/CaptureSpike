using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CaptureSpike.App;

/// <summary>
/// One window showing all of a take's video sources in a tile grid.
/// Each tile hosts an embedded mpv via <see cref="MpvVideoHost"/>.
/// Audio sources play headless (no tile).
/// </summary>
public sealed class TiledPlaybackWindow : Window
{
    private sealed class Tile
    {
        public required string SourceId;
        public required Border Border;
        public required TaskCompletionSource<nint> HandleTcs;
    }

    private readonly List<Tile> _tiles = new();

    public TiledPlaybackWindow(string title, IReadOnlyList<(string SourceId, string Label, string Badge)> videoSources)
    {
        Title = title;
        Width = 1280;
        Height = 720;

        if (videoSources.Count == 0)
        {
            Content = new TextBlock
            {
                Text = "No video sources in this take.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }

        var cols = (int)Math.Ceiling(Math.Sqrt(videoSources.Count));
        var rows = (int)Math.Ceiling(videoSources.Count / (double)cols);

        var grid = new Grid();
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        for (var i = 0; i < videoSources.Count; i++)
        {
            var (sourceId, label, badge) = videoSources[i];
            var host = new MpvVideoHost();
            var tcs = new TaskCompletionSource<nint>();
            host.HandleCreated += h => tcs.TrySetResult(h);
            host.HandleDestroyed += () => tcs.TrySetCanceled();

            var caption = new TextBlock
            {
                Text = $"{badge} · {label}",
                FontSize = 13,
                Padding = new Thickness(6, 2),
                Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var panel = new Panel { Background = Brushes.Black };
            panel.Children.Add(host);
            panel.Children.Add(caption);

            var border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Child = panel,
            };

            Grid.SetRow(border, i / cols);
            Grid.SetColumn(border, i % cols);
            grid.Children.Add(border);

            _tiles.Add(new Tile { SourceId = sourceId, Border = border, HandleTcs = tcs });
        }

        Content = grid;
    }

    /// <summary>Handles for each tile's native child, in tile order. 0 = embedding failed.</summary>
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
}
