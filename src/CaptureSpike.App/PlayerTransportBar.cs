using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CaptureSpike.App;

/// <summary>
/// Transport bar: Play/Pause toggle, Stop button, time label, slider.
/// Docked at the bottom of the tiled playback window. Reads state from
/// an <see cref="ITransportHost"/>; never mutates playback directly.
///
/// Owns a DispatcherTimer that polls the host's Position / IsPlaying
/// every 200 ms. Polling (rather than event-driven) keeps this simple —
/// the host's IPlayer already pushes property changes via the existing
/// PlayerPositionTimer in MainViewModel, and this view just renders.
/// </summary>
public sealed class PlayerTransportBar : ContentControl
{
    private readonly Button _playPauseBtn;
    private readonly Button _stopBtn;
    private readonly TextBlock _timeText;
    private readonly Slider _slider;
    private readonly DispatcherTimer _poll;
    private ITransportHost? _host;
    private bool _scrubbing;

    public PlayerTransportBar()
    {
        _playPauseBtn = new Button
        {
            FontSize = 15,
            Padding = new Thickness(8, 2),
            MinWidth = 60,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _playPauseBtn.Click += (_, _) => _host?.PlayPause();

        _stopBtn = new Button
        {
            Content = "■",
            FontSize = 15,
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 8, 0),
        };
        ToolTip.SetTip(_stopBtn, "Stop and close playback");
        _stopBtn.Click += (_, _) => _host?.Stop();

        _timeText = new TextBlock
        {
            FontFamily = new FontFamily("Monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _slider = new Slider
        {
            VerticalAlignment = VerticalAlignment.Center,
            Minimum = 0,
        };
        _slider.PointerPressed += OnSliderPressed;
        _slider.PointerReleased += OnSliderReleased;

        // Layout: a DockPanel so the buttons sit on the left, slider
        // stretches, time label pins to the right.
        var dock = new DockPanel();
        DockPanel.SetDock(_timeText, Dock.Right);
        DockPanel.SetDock(_stopBtn, Dock.Left);
        DockPanel.SetDock(_playPauseBtn, Dock.Left);
        dock.Children.Add(_timeText);
        dock.Children.Add(_stopBtn);
        dock.Children.Add(_playPauseBtn);
        dock.Children.Add(_slider);

        // Wrap in a Border so it has a top divider line that adapts to
        // the theme via the DividerBrush resource.
        var border = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Brush(ThemeKeys.Divider),
            Padding = new Thickness(8, 6),
            Child = dock,
        };
        Content = border;

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _poll.Tick += (_, _) => RefreshFromHost();
    }

    /// <summary>Bind to the playback state. Call before the window is shown.</summary>
    public void Attach(ITransportHost host)
    {
        _host = host;
        RefreshFromHost();
        _poll.Start();
    }

    /// <summary>Stops polling; safe to call multiple times.</summary>
    public void Detach()
    {
        _poll.Stop();
        _host = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshFromHost()
    {
        if (_host is null) return;
        var pos = _host.Position;
        var dur = _host.Duration;
        _playPauseBtn.Content = _host.PlayPauseGlyph;
        _timeText.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";
        _slider.Maximum = Math.Max(0, dur.TotalSeconds);
        if (!_scrubbing)
            _slider.Value = Math.Max(0, pos.TotalSeconds);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        _scrubbing = true;
    }

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_host is not null && _slider.Value >= 0)
            _host.SeekTo(_slider.Value);
        _scrubbing = false;
    }

    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush
        ?? new SolidColorBrush(Color.FromRgb(60, 60, 60));
}