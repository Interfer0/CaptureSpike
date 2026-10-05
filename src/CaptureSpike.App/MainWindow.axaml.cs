using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CaptureSpike.App;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
