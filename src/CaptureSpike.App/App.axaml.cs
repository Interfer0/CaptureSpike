using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CaptureSpike.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Global safety net: log and show instead of crashing outright.
        // Individual operations should still try/catch and report via Status;
        // this catches what slips through (e.g., async void event handlers).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash(e.ExceptionObject as Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            SetupTrayIcon(desktop, (Window)desktop.MainWindow, vm);
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
            // Write to a crash log next to settings for diagnosis.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CaptureSpike");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
        }
        catch { }

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
