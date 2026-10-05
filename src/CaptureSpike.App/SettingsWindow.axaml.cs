using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CaptureSpike.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        FolderBox.Text = AppSettings.ProjectsDir;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose projects folder" });
        if (folders.Count > 0)
            FolderBox.Text = folders[0].Path.LocalPath;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var dir = FolderBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(dir))
            AppSettings.ProjectsDir = dir;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
