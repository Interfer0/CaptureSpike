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
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void ReportCrash(Exception? ex)
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
