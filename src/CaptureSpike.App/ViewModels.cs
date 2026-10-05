using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CaptureSpike.Core;
using CaptureSpike.Core.Capture;

namespace CaptureSpike.App;

#region plumbing

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? _) => canExecute?.Invoke() ?? true;
    public void Execute(object? _) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal static class AppSettings
{
    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CaptureSpike", "settings.json");

    public static string? LastProjectDir
    {
        get
        {
            try
            {
                if (!File.Exists(Path_)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
                return doc.RootElement.TryGetProperty("lastProjectDir", out var p)
                    ? p.GetString() : null;
            }
            catch { return null; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
                File.WriteAllText(Path_, JsonSerializer.Serialize(new { lastProjectDir = value }));
            }
            catch { /* settings are best-effort */ }
        }
    }
}

#endregion

public sealed class SceneViewModel(Scene scene, Action save) : ObservableObject
{
    public Scene Scene { get; } = scene;

    public string Title
    {
        get => Scene.Title;
        set { Scene.Title = value; save(); Raise(); }
    }

    public string Notes
    {
        get => Scene.Notes;
        set { Scene.Notes = value; save(); Raise(); }
    }

    public string DisplayName => $"{Scene.Order + 1}. {Scene.Title}";
    public void RefreshOrder() => Raise(nameof(DisplayName));
}

public sealed class TakeRow : ObservableObject
{
    public TakeRow(Take take, bool isFavorite)
    {
        Take = take;
        IsFavorite = isFavorite;
    }

    public Take Take { get; }
    public bool IsFavorite { get; }

    public string Display
    {
        get
        {
            var star = IsFavorite ? "★ " : "";
            var dur = TimeSpan.FromSeconds(Take.DurationSeconds).ToString(@"m\:ss");
            return $"{star}{Take.Id} · {Take.Sources.Count} sources · {dur}";
        }
    }
}

public sealed class MainViewModel : ObservableObject
{
    private ProjectStore? _store;

    public ObservableCollection<SceneViewModel> Scenes { get; } = new();
    public ObservableCollection<TakeRow> Takes { get; } = new();

    private SceneViewModel? _selectedScene;
    public SceneViewModel? SelectedScene
    {
        get => _selectedScene;
        set { if (SetField(ref _selectedScene, value)) RefreshTakes(); }
    }

    private TakeRow? _selectedTake;
    public TakeRow? SelectedTake
    {
        get => _selectedTake;
        set { if (SetField(ref _selectedTake, value)) Raise(nameof(IsFavorite)); }
    }

    private string _status = "No project open.";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private string _projectName = "";
    public string ProjectName { get => _projectName; set => SetField(ref _projectName, value); }

    public bool HasProject => _store is not null;

    public ICommand NewProjectCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand AddSceneCommand { get; }
    public ICommand DeleteSceneCommand { get; }
    public ICommand MoveSceneUpCommand { get; }
    public ICommand MoveSceneDownCommand { get; }
    public ICommand DeleteTakeCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleRecordCommand { get; }

    /// <summary>Swappable for tests. Defaults to the real ffmpeg/NAudio recorder.</summary>
    public Func<ProjectStore, IRecorder> RecorderFactory { get; set; } =
        store => new Recorder(store);

    private IRecordingSession? _session;
    public bool IsRecording => _session is not null;
    public string RecordButtonText => IsRecording ? "■ Stop" : "● Record";

    private string _recordingElapsed = "";
    public string RecordingElapsed
    {
        get => _recordingElapsed;
        set => SetField(ref _recordingElapsed, value);
    }

    public MainViewModel()
    {
        NewProjectCommand = new RelayCommand(NewProject, () => !IsRecording);
        OpenProjectCommand = new RelayCommand(OpenProjectAsync, () => !IsRecording);
        AddSceneCommand = new RelayCommand(() => Mutate(AddScene), () => HasProject);
        DeleteSceneCommand = new RelayCommand(() => Mutate(DeleteScene), () => HasProject && SelectedScene is not null);
        MoveSceneUpCommand = new RelayCommand(() => Mutate(() => MoveScene(-1)), () => HasProject && SelectedScene is not null);
        MoveSceneDownCommand = new RelayCommand(() => Mutate(() => MoveScene(1)), () => HasProject && SelectedScene is not null);
        DeleteTakeCommand = new RelayCommand(() => Mutate(DeleteTake), () => HasProject && SelectedTake is not null);
        ToggleFavoriteCommand = new RelayCommand(() => Mutate(ToggleFavorite), () => HasProject && SelectedTake is not null);
        ToggleRecordCommand = new RelayCommand(ToggleRecordAsync,
            () => HasProject && (IsRecording || SelectedScene is not null));

        var last = AppSettings.LastProjectDir;
        if (last is not null && Directory.Exists(last))
            OpenProject(last);
        else
            NewProject(); // first run: create a starter project so the UI isn't empty
    }

    private void Mutate(Action action)
    {
        action();
        foreach (var c in new[] { AddSceneCommand, DeleteSceneCommand, MoveSceneUpCommand,
                                  MoveSceneDownCommand, DeleteTakeCommand, ToggleFavoriteCommand,
                                  ToggleRecordCommand, NewProjectCommand, OpenProjectCommand }
                 .OfType<RelayCommand>())
            c.RaiseCanExecuteChanged();
        Raise(nameof(HasProject));
    }

    // ----- projects -----

    private string DefaultProjectsDir()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
            videos = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        return Path.Combine(videos, "CaptureSpike");
    }

    private void NewProject()
    {
        var dir = Path.Combine(DefaultProjectsDir(),
            $"Tutorial-{DateTime.Now:yyyyMMdd-HHmmss}.csp");
        var store = ProjectStore.Create(dir, "Untitled Tutorial");
        store.AddScene("Intro");
        store.AddScene("Demo");
        OpenProject(dir);
        Status = $"Created {dir}";
    }

    private async void OpenProjectAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var folders = await desktop.MainWindow!.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Open CaptureSpike project", AllowMultiple = false });
        if (folders.Count == 0) return;
        try { OpenProject(folders[0].Path.LocalPath); }
        catch (Exception ex) { Status = $"Couldn't open project: {ex.Message}"; }
    }

    private void OpenProject(string dir)
    {
        _store = ProjectStore.Open(dir);
        AppSettings.LastProjectDir = dir;
        ProjectName = _store.Project.Name;
        ReloadScenes();
        Status = $"Project: {dir}";
        Raise(nameof(HasProject));
    }

    // ----- scenes -----

    private void ReloadScenes()
    {
        Scenes.Clear();
        if (_store is null) return;
        foreach (var s in _store.LoadScenes())
            Scenes.Add(new SceneViewModel(s, () => _store.SaveScene(s)));
        SelectedScene = Scenes.FirstOrDefault();
        foreach (var vm in Scenes) vm.RefreshOrder();
    }

    private void AddScene()
    {
        if (_store is null) return;
        var scene = _store.AddScene($"Scene {Scenes.Count + 1}");
        var vm = new SceneViewModel(scene, () => _store.SaveScene(scene));
        Scenes.Add(vm);
        SelectedScene = vm;
        Status = $"Added scene \"{scene.Title}\"";
    }

    private void DeleteScene()
    {
        if (_store is null || SelectedScene is null) return;
        var title = SelectedScene.Title;
        _store.DeleteScene(SelectedScene.Scene);
        ReloadScenes();
        Status = $"Deleted scene \"{title}\"";
    }

    private void MoveScene(int delta)
    {
        if (_store is null || SelectedScene is null) return;
        var vm = SelectedScene;
        _store.MoveScene(vm.Scene, vm.Scene.Order + delta);
        ReloadScenes();
        SelectedScene = Scenes.FirstOrDefault(s => s.Scene.Id == vm.Scene.Id);
    }

    // ----- takes -----

    private void RefreshTakes()
    {
        Takes.Clear();
        SelectedTake = null;
        if (_store is null || SelectedScene is null) return;
        var fav = SelectedScene.Scene.FavoriteTakeId;
        foreach (var t in _store.LoadTakes(SelectedScene.Scene.Id))
            Takes.Add(new TakeRow(t, t.Id == fav));
    }

    private void DeleteTake()
    {
        if (_store is null || SelectedScene is null || SelectedTake is null) return;
        var id = SelectedTake.Take.Id;
        _store.DeleteTake(SelectedScene.Scene.Id, SelectedTake.Take);
        RefreshTakes();
        Status = $"Deleted take {id}";
    }

    private void ToggleFavorite()
    {
        if (_store is null || SelectedScene is null || SelectedTake is null) return;
        var scene = SelectedScene.Scene;
        var id = SelectedTake.Take.Id;
        scene.FavoriteTakeId = scene.FavoriteTakeId == id ? null : id;
        _store.SaveScene(scene);
        RefreshTakes();
    }

    public bool IsFavorite =>
        SelectedScene is not null && SelectedTake is not null &&
        SelectedScene.Scene.FavoriteTakeId == SelectedTake.Take.Id;

    // ----- recording -----

    private async void ToggleRecordAsync()
    {
        if (_store is null) return;

        if (_session is not null)
        {
            var session = _session;
            _session = null;
            RaiseRecordingState();
            try
            {
                var take = await session.StopAsync();
                await session.DisposeAsync();
                RefreshTakes();
                Status = $"Take {take.Id} saved — {take.Sources.Count} sources, {take.DurationSeconds:F1}s";
            }
            catch (Exception ex)
            {
                Status = $"Stop failed: {ex.Message}";
            }
            return;
        }

        if (SelectedScene is null) return;
        try
        {
            var scene = SelectedScene.Scene;
            var takeDir = _store.BeginTake(scene);
            var progress = new Progress<TimeSpan>(t => RecordingElapsed = t.ToString(@"m\:ss"));
            _session = await RecorderFactory(_store).StartAsync(_store.Project, scene, takeDir, progress);
            RaiseRecordingState();
            Status = $"Recording '{scene.Title}' → {Path.GetFileName(takeDir)}";
        }
        catch (Exception ex)
        {
            Status = $"Record failed: {ex.Message}";
        }
    }

    private void RaiseRecordingState()
    {
        Raise(nameof(IsRecording));
        Raise(nameof(RecordButtonText));
        if (!IsRecording) RecordingElapsed = "";
        foreach (var c in new[] { ToggleRecordCommand, NewProjectCommand, OpenProjectCommand }
                 .OfType<RelayCommand>())
            c.RaiseCanExecuteChanged();
    }
}
