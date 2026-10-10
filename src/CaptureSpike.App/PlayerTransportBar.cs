using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    // Captured during PointerPressed → PointerReleased so the seek
    // uses the value the user actually chose. Reading _slider.Value
    // directly at release is unreliable in Avalonia 12 (the property
    // can be one frame behind the visual thumb when the user clicks
    // somewhere on the track, or stale while a drag is in flight).
    private double _scrubTarget;
    // True from the moment we issue a seek until the player's Position
    // catches up. While set, RefreshFromHost ignores the polled position
    // and lets the slider keep the user's chosen value, so backward
    // scrubs no longer snap back to the live playhead.
    private bool _awaitingSeek;

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
        // Capture every value change between PointerPressed and
        // PointerReleased. This way the seek uses the exact value the
        // user chose even if Avalonia's slider state is a frame behind.
        _slider.PointerPressed += OnSliderPressed;
        _slider.PointerReleased += OnSliderReleased;
        _slider.ValueChanged += OnSliderValueChanged;
        // If the pointer leaves the slider mid-drag (e.g., focus stolen,
        // drag off the thumb) PointerReleased may never fire — release
        // the scrub flag so the slider doesn't get stuck.
        _slider.PointerCaptureLost += (_, _) => EndScrubFromBar();

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
        // Two guards keep the slider from snapping:
        //   _scrubbing   — the user is mid-drag, don't fight the thumb.
        //   _awaitingSeek — a seek has been issued and the player's
        //                   Position hasn't caught up yet. Letting the
        //                   polled position through here would drag
        //                   the slider back to the pre-seek location
        //                   and was the source of the "backward seek
        //                   doesn't work" bug. The flag is cleared
        //                   when the slider's Value converges on the
        //                   polled position (EndScrubFromBar).
        if (_scrubbing) return;
        // If we're awaiting a seek, decide whether to keep the slider
        // frozen or resume tracking the live position.
        //
        // We resume only when the polled position is within ±0.25s of
        // the user's chosen target. That's the moment mpv reports the
        // new time-pos. We deliberately don't have a "played past the
        // target" early-out: if the user rewinds to 20 and the player
        // was at 30, they want the slider at 20, not 30 — letting the
        // polled position through while pos is still ahead of target
        // would snap the slider back to 30 (the bug behind "I have to
        // click several times to get it to work").
        if (_awaitingSeek)
        {
            var posSec = pos.TotalSeconds;
            if (Math.Abs(posSec - _scrubTarget) < 0.25)
            {
                _awaitingSeek = false;
                DebugScrub($"awaiting: cleared pos={posSec:F2} target={_scrubTarget:F2}");
            }
            else
            {
                DebugScrub($"awaiting: holding pos={posSec:F2} target={_scrubTarget:F2}");
                return; // still settling — leave the slider alone
            }
        }
        var before = _slider.Value;
        _slider.Value = Math.Max(0, pos.TotalSeconds);
        DebugScrub($"refresh: slider {before:F2} -> {_slider.Value:F2} (pos={pos.TotalSeconds:F2})");
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        _scrubbing = true;
        // Seed the target with the value the user just clicked on so
        // OnSliderReleased can use it even if no ValueChanged event
        // fires between press and release (e.g., a static click on
        // a single pixel where the thumb already sits).
        _scrubTarget = _slider.Value;
        DebugScrub($"press: target={_scrubTarget:F2} slider={_slider.Value:F2}");
    }

    private void OnSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Track the slider's value while scrubbing so a quick press+click
        // on the track (where Avalonia may fire ValueChanged slightly out
        // of order with PointerReleased) still lands on the right target.
        if (_scrubbing)
            _scrubTarget = e.NewValue;
        DebugScrub($"value-changed: {e.OldValue:F2} -> {e.NewValue:F2} scrubbing={_scrubbing} target={_scrubTarget:F2}");
    }

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        DebugScrub($"release: slider={_slider.Value:F2} target={_scrubTarget:F2}");
        EndScrubFromBar();
    }

    /// <summary>
    /// Issue the seek to the value the user chose, then keep the slider
    /// frozen at that value until the player's position catches up.
    /// </summary>
    private void EndScrubFromBar()
    {
        _scrubbing = false;
        if (_host is null) return;
        // Re-read the slider's value at the moment of release. This
        // protects against races where Avalonia's ValueChanged event
        // fires after PointerReleased — without this, we'd seek to a
        // stale _scrubTarget from when the press started, and forward
        // scrubs (where the user clicks past the thumb and releases
        // quickly) would appear not to work.
        var v = _slider.Value;
        if (v >= 0) _scrubTarget = v;
        if (_scrubTarget < 0) { _scrubTarget = 0; }
        DebugScrub($"seek: target={_scrubTarget:F2}");
        _host.SeekTo(_scrubTarget);
        // Hold the slider at the user's chosen value until the
        // player's polled Position converges — see _awaitingSeek in
        // RefreshFromHost. Cleared there once the position lands.
        _awaitingSeek = true;
    }

    // Diagnostic logging for slider seek debugging. Off by default; flip
    // CAPTURESPIKE_SLIDER_TRACE=1 in the environment to enable. Writes
    // through the app's log panel and the on-disk session log so users
    // can paste the trace straight into chat.
    private static bool _sliderTraceEnabled =
        System.Environment.GetEnvironmentVariable("CAPTURESPIKE_SLIDER_TRACE") == "1";
    private void DebugScrub(string msg)
    {
        if (!_sliderTraceEnabled) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.DataContext is not MainViewModel vm) return;
        vm.Log($"[slider] {msg}");
    }

    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.FindResource(key) as IBrush
        ?? new SolidColorBrush(Color.FromRgb(60, 60, 60));
}