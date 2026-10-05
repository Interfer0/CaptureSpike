using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace CaptureSpike.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var host = this.FindControl<MpvVideoHost>("VideoHost");
        if (host is not null)
        {
            host.HandleCreated += h =>
            {
                if (DataContext is MainViewModel vm) vm.VideoHandle = h;
            };
            host.HandleDestroyed += () =>
            {
                if (DataContext is MainViewModel vm) vm.VideoHandle = nint.Zero;
            };
        }
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
}
