// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace CaptureSpike.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        RestoreWindowBounds();
        // App icon (optional: only if the asset was synced).
        // Prefer the .ico (multi-resolution) over the PNG;
        // Windows picks the right size from the .ico for
        // the title bar / taskbar without upscaling.
        try
        {
            Icon = new WindowIcon(
                Avalonia.Platform.AssetLoader.Open(new Uri("avares://CaptureSpike.App/Assets/capturespike-icon.ico")));
        }
        catch
        {
            try
            {
                Icon = new WindowIcon(
                    Avalonia.Platform.AssetLoader.Open(new Uri("avares://CaptureSpike.App/Assets/capturespike-icon.png")));
            }
            catch { /* default icon */ }
        }

        Opened += (_, _) =>
        {
            EnsureOnScreen();
            if (DataContext is MainViewModel vm)
            {
                vm.StartupAsync();
                // Keep the log pinned to the newest line.
                var logBox = this.FindControl<ListBox>("LogListBox");
                if (logBox is not null)
                {
                    vm.LogLines.CollectionChanged += (_, e) =>
                    {
                        if (e.NewItems?.Count > 0)
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                logBox.ScrollIntoView(e.NewItems[^1]!));
                    };
                }
            }
        };

        // Don't lose the take if the window is closed mid-recording:
        // stop gracefully (writes take.json, finalizes files) before closing.
        //
        // IMPORTANT: Avalonia's Closing handler can't
        // truly wait for async work — setting e.Cancel
        // and then awaiting in the handler doesn't
        // actually defer the close. The window closes
        // as soon as the handler returns, regardless
        // of e.Cancel. So we use a different pattern:
        // call Hide() to keep the app alive, stop the
        // recording asynchronously, then Close() once
        // the stop is complete.
        Closing += async (_, e) =>
        {
            if (DataContext is MainViewModel vm && vm.IsRecording)
            {
                e.Cancel = true;
                Hide();
                try { await vm.StopRecordingAsync(); }
                catch { /* Status already reports the failure */ }
                finally { Close(); }
            }
        };

        // Playback is shown as a separate top-level window. Close it only
        // when this close request is not being deferred for recording shutdown.
        Closing += (_, e) =>
        {
            if (!e.Cancel && DataContext is MainViewModel vm)
                vm.ClosePlaybackWindow();
        };

        // Remember size, position, and maximized state for next launch.
        Closing += (_, _) =>
        {
            AppSettings.SaveWindowBounds(Position.X, Position.Y, Width, Height,
                WindowState == WindowState.Maximized);
        };
    }

    /// <summary>
    /// Restores the last window bounds. First run (nothing saved) starts
    /// maximized, as before.
    /// </summary>
    private void RestoreWindowBounds()
    {
        var b = AppSettings.LoadWindowBounds();
        if (b is null || b.Maximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }
        if (b.Width > 100 && b.Height > 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = b.Width;
            Height = b.Height;
            Position = new PixelPoint((int)b.X, (int)b.Y);
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// If the saved position is on a monitor that's no longer connected,
    /// center the window on the primary screen instead of opening off-screen.
    /// </summary>
    private void EnsureOnScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromPoint(Position);
        if (screen is not null) return;
        var primary = Screens.Primary;
        if (primary is null) return;
        var area = primary.WorkingArea;
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - (int)Width) / 2),
            area.Y + Math.Max(0, (area.Height - (int)Height) / 2));
    }

    private void OnSeekPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // Seek lives in the tiled playback window's PlayerTransportBar
        // now; this handler stays here so the XAML compiles cleanly if
        // someone re-adds a slider to the main window.
        if (DataContext is MainViewModel vm) vm.BeginScrub();
    }

    private void OnSeekPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Slider slider)
            vm.EndScrub(slider.Value);
    }

    private void OnSeekGotFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.BeginScrub();
    }

    private void OnSeekLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Slider slider)
            vm.EndScrub(slider.Value);
    }
}