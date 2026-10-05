using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CaptureSpike.App;

public partial class ExportWindow : Window
{
    public ExportWindow() => InitializeComponent();

    public void SetScenes(IEnumerable<string> lines) =>
        SceneList.ItemsSource = lines.ToList();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
