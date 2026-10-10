using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CaptureSpike.Core;

using System.Threading.Tasks;

namespace CaptureSpike.App;

/// <summary>One project folder in the library.</summary>
public sealed record ProjectEntry(string Name, string Dir, string Info);

public partial class ProjectBrowserWindow : Window
{
    private readonly Action<string>? _openProject;
    private readonly Action<string>? _createProject;
    private readonly Func<string, Task>? _renameProject;
    private readonly Func<string, Task>? _deleteProject;

    /// <summary>Parameterless ctor used by the Avalonia XAML loader.</summary>
    public ProjectBrowserWindow()
    {
        InitializeComponent();
        LibraryLabel.Text = $"Library: {AppSettings.ProjectsDir}";
        ProjectList.DoubleTapped += (_, _) => OpenSelected();
        RefreshList();
    }

    public ProjectBrowserWindow(
        Action<string> openProject,
        Action<string> createProject,
        Func<string, Task> renameProject,
        Func<string, Task> deleteProject)
        : this()
    {
        _openProject = openProject;
        _createProject = createProject;
        _renameProject = renameProject;
        _deleteProject = deleteProject;
    }

    private void RefreshList()
    {
        var entries = new List<ProjectEntry>();
        var root = AppSettings.ProjectsDir;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, "project.json"))) continue;
                try
                {
                    var store = ProjectStore.Open(dir);
                    var scenes = store.LoadScenes();
                    var takes = scenes.Sum(s => store.LoadTakes(s.FolderName).Count);
                    var modified = Directory.GetLastWriteTime(dir);
                    entries.Add(new ProjectEntry(
                        store.Project.Name,
                        dir,
                        $"{scenes.Count} scene{(scenes.Count == 1 ? "" : "s")} · " +
                        $"{takes} take{(takes == 1 ? "" : "s")} · " +
                        $"modified {modified:MMM d, yyyy}"));
                }
                catch { /* skip unreadable projects */ }
            }
        }
        ProjectList.ItemsSource = entries
            .OrderByDescending(e => Directory.GetLastWriteTime(e.Dir))
            .ToList();
    }

    private void OpenSelected()
    {
        if (ProjectList.SelectedItem is ProjectEntry entry && _openProject is not null)
        {
            _openProject(entry.Dir);
            Close();
        }
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (_openProject is not null) OpenSelected();
        else Close();
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectEntry entry) return;
        if (_renameProject is null) return;
        // Hand the rename off to the VM and refresh the list only
        // when the VM is done — otherwise we'd read the project.json
        // before the new name has been written to disk.
        _renameProject(entry.Dir).ContinueWith(_ =>
        {
            // Back on the UI thread to mutate the ListBox.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RefreshList();
                // Re-select the entry that was just renamed so a follow-up
                // Rename click operates on the same row.
                foreach (var item in ProjectList.Items)
                {
                    if (item is ProjectEntry e2 && e2.Dir == entry.Dir)
                    {
                        ProjectList.SelectedItem = item;
                        break;
                    }
                }
            });
        }, TaskScheduler.Default);
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        _createProject?.Invoke(NameBox.Text?.Trim() ?? "");
        Close();
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectEntry entry) return;
        if (_deleteProject is null) return;
        // Confirm with the user — deleting a project is irreversible
        // (folder + every scene + every take + recordings).
        var dialog = new Window
        {
            Title = "Delete project",
            Width = 460, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Delete project \"{entry.Name}\"?",
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"This deletes the folder on disk and every scene, take, " +
                   $"and recording inside it. There is no undo.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new Button { Content = "Cancel" };
        var delete = new Button { Content = "Delete" };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        delete.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(delete);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        try { await dialog.ShowDialog(this); } catch { tcs.TrySetResult(false); }
        if (await tcs.Task != true) return;

        try
        {
            await _deleteProject(entry.Dir);
            // Refresh the list and clear the selection — the row we
            // were on no longer exists. Don't auto-close; the user
            // may want to delete more.
            RefreshList();
            ProjectList.SelectedItem = null;
        }
        catch (Exception ex)
        {
            // Surface a failure rather than silently swallowing it.
            await new Window
            {
                Title = "Delete failed",
                Width = 380, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock
                {
                    Text = $"Couldn't delete the project:\n\n{ex.Message}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(16),
                },
            }.ShowDialog(this);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
