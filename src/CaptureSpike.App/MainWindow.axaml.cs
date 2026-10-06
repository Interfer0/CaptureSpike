using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace CaptureSpike.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        WindowState = WindowState.Maximized;
        // App icon (optional: only if the asset was synced).
        try
        {
            Icon = new WindowIcon(
                Avalonia.Platform.AssetLoader.Open(new Uri("avares://CaptureSpike.App/Assets/capturespike-icon.png")));
        }
        catch { /* default icon */ }

        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.StartupAsync();
        };
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.BeginScrub();
    }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
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
