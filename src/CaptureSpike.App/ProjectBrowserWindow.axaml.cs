using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CaptureSpike.Core;

namespace CaptureSpike.App;

/// <summary>One project folder in the library.</summary>
public sealed record ProjectEntry(string Name, string Dir, string Info);

public partial class ProjectBrowserWindow : Window
{
    private readonly Action<string>? _openProject;
    private readonly Action<string>? _createProject;
    private readonly Action<string>? _renameProject;

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
        Action<string> renameProject)
        : this()
    {
        _openProject = openProject;
        _createProject = createProject;
        _renameProject = renameProject;
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
        _renameProject(entry.Dir);
        // The VM's rename swaps the project store behind our back
        // (and may reopen the original). Refresh so the list reflects
        // the new name; if the renamed project happens to be the
        // currently-open one, it'll show the new name on next paint.
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
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        _createProject?.Invoke(NameBox.Text?.Trim() ?? "");
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
