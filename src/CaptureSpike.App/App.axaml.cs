using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using CaptureSpike.Core;

namespace CaptureSpike.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The global palette lives in the CaptureSpike data folder
        // (next to the .csp project subfolders), not in %APPDATA%.
        // Core resolves the path via this env var so the App owns
        // the directory location without Core needing to depend on
        // AppSettings.
        System.Environment.SetEnvironmentVariable(
            "CAPTURESPIKE_PALETTE_DIR", AppSettings.ProjectsDir);
        // Apply the saved theme before any windows are constructed so
        // brushes/colors are correct from the first frame. New users
        // (no settings.json yet) get the app's default of Dark.
        var saved = AppSettings.Theme;
        if (saved is null && !File.Exists(AppSettings.SettingsFilePath))
        {
            saved = "Dark";
            AppSettings.Theme = saved;
        }
        RequestedThemeVariant = ThemeKeys.ToVariant(saved);

        // Global safety net: log and show instead of crashing outright.
        // Individual operations should still try/catch and report via Status;
        // this catches what slips through (e.g., async void event handlers).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            AppLog.Open().WriteCrash(ex ?? new Exception("Unknown unhandled exception"));
            ReportCrash(ex);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(new AvaloniaClipboardWriter());
            // Initialize the session log immediately and surface the
            // folder in the status bar so the user can find it without
            // hunting around.
            AppLog.Open();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            SetupTrayIcon(desktop, (Window)desktop.MainWindow, vm);
            vm.Status = $"Log folder: {AppLog.LogDirectory}";
            vm.Log($"Session started. Log folder: {AppLog.LogDirectory}");
        }
        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcons? _trayIcons;

    /// <summary>
    /// System tray icon: the reliable way to stop a recording while the
    /// window is minimized (replaces the old F9 hotkey).
    /// </summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop,
        Window window, MainViewModel vm)
    {
        WindowIcon? icon = null;
        try
        {
            icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://CaptureSpike.App/Assets/capturespike-icon.png")));
        }
        catch { /* tray without icon */ }

        var stopItem = new NativeMenuItem("Stop Recording");
        stopItem.Click += (_, _) => vm.StopRecordingFromTray();
        var showItem = new NativeMenuItem("Show Window");
        showItem.Click += (_, _) => vm.ShowMainWindow();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(stopItem);
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);

        var tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "CaptureSpike",
            Menu = menu,
        };
        // Left-click toggles the window.
        tray.Clicked += (_, _) =>
        {
            if (window.IsVisible && window.WindowState != WindowState.Minimized)
                window.Hide();
            else
                vm.ShowMainWindow();
        };

        _trayIcons = new TrayIcons { tray };
        TrayIcon.SetIcons(this, _trayIcons);

        // Keep the menu and tooltip in sync with recording state.
        void Refresh()
        {
            var active = vm.IsRecording || vm.IsStarting;
            stopItem.IsEnabled = active;
            stopItem.Header = vm.IsStarting ? "Cancel Recording" : "Stop Recording";
            tray.ToolTipText = vm.IsRecording
                ? $"● CaptureSpike — recording {vm.RecordingElapsed}"
                : "CaptureSpike";
        }
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsRecording)
                or nameof(MainViewModel.IsStarting)
                or nameof(MainViewModel.RecordingElapsed))
                Dispatcher.UIThread.Post(Refresh);
        };
        Refresh();
    }

    internal static void ReportCrash(Exception? ex)
    {
        var message = ex?.Message ?? "Unknown error";
        try
        {
            // Write to the consolidated crash.log in the log folder so the
            // user has exactly one place to look. AppLog handles its own
            // disk failures silently — see AppLog.WriteCrash.
            AppLog.Open().WriteCrash(ex ?? new Exception("Unknown error"));
        }
        catch { /* never let logging take the app down */ }

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                var window = (Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (window is not null)
                {
                    // Also land in the in-app log panel.
                    if (window.DataContext is MainViewModel vm)
                        vm.Log($"CRASH: {message}");
                    // Best-effort: show what happened without taking the app down.
                    var dialog = new Window
                    {
                        Title = "Something went wrong",
                        Width = 480, Height = 200,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    };
                    dialog.Content = new TextBlock
                    {
                        Text = $"CaptureSpike hit an unexpected error:\n\n{message}\n\nThe app will try to continue. Details are in crash.log.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(16),
                    };
                    dialog.ShowDialog(window);
                }
            });
        }
        catch { }
    }
}

/// <summary>
/// Avalonia-backed implementation of <see cref="IClipboardWriter"/>.
/// Resolves the main window lazily on first use so the writer can be
/// constructed before the window has finished loading. Falls back to
/// clipboard=noop if no window is available (e.g., during shutdown or
/// in unusual lifetime modes).
/// </summary>
internal sealed class AvaloniaClipboardWriter : IClipboardWriter
{
    public Task SetTextAsync(string text) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
    {
        try
        {
            var clipboard = TryGetClipboard();
            if (clipboard is null)
            {
                LogClipboardSkip("no main window yet");
                return;
            }
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            // Last-resort surface: the command's own try/catch should have
            // caught this, but if we got here something is deeply wrong.
            App.ReportCrash(ex);
        }
    });

    private static Avalonia.Input.Platform.IClipboard? TryGetClipboard()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return window?.Clipboard;
    }

    private static void LogClipboardSkip(string reason)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.DataContext is MainViewModel vm)
            vm.Log($"Copy skipped: {reason}");
    }
}
