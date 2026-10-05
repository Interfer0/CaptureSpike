using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CaptureSpike.Core;
using CaptureSpike.Core.Capture;
using CaptureSpike.Core.Playback;

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
        set { Scene.Title = value; save(); Raise(); Raise(nameof(SceneHeader)); }
    }

    public string Notes
    {
        get => Scene.Notes;
        set { Scene.Notes = value; save(); Raise(); Raise(nameof(WordCountText)); }
    }

    public string SceneHeader => $"{Scene.Title} · Scene {Scene.Order + 1:D2}";
    public string DisplayName => $"{Scene.Order + 1:D2} {Scene.Title}";

    private int _takeCount;
    public int TakeCount
    {
        get => _takeCount;
        set => SetField(ref _takeCount, value);
    }

    public void RefreshOrder()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(SceneHeader));
    }

    /// <summary>"56 words · about 0:22 spoken" at ~150 wpm.</summary>
    public string WordCountText
    {
        get
        {
            var words = string.IsNullOrWhiteSpace(Scene.Notes) ? 0 :
                Scene.Notes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var spoken = TimeSpan.FromSeconds(words * 60.0 / 150);
            return $"{words} word{(words == 1 ? "" : "s")} · about {spoken:m\\:ss} spoken";
        }
    }
}

public sealed class PaletteSourceViewModel(ProjectSource source) : ObservableObject
{
    public ProjectSource Source { get; } = source;
    public string Badge => Source.Kind.Badge();
    public string Label => Source.Label;
    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand DeleteCommand { get; set; } = null!;
}

public sealed class SceneSourceViewModel(SceneSource sceneSource, ProjectSource palette)
    : ObservableObject
{
    public SceneSource SceneSource { get; } = sceneSource;
    public string Badge => palette.Kind.Badge();
    public string Label => palette.Label;
    public bool IsEnabled => SceneSource.Enabled;
    public string Dot => SceneSource.Enabled ? "🟢" : "⚪";

    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand ToggleCommand { get; set; } = null!;
    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand RemoveCommand { get; set; } = null!;

    public void Refresh() { Raise(nameof(IsEnabled)); Raise(nameof(Dot)); }
}

public sealed class PlayerTrackRow(TakeSource source, string label) : ObservableObject
{
    public TakeSource Source { get; } = source;
    public string SourceId => Source.Id;
    public string Badge => Source.Kind.Badge();
    public string Label => label;
    public bool IsVideo => Source.Kind.IsVideo();

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                Raise(nameof(MuteIcon));
                Raise(nameof(MuteTip));
            }
        }
    }

    public string MuteIcon => IsVideo
        ? (IsMuted ? "🚫" : "👁")
        : (IsMuted ? "🔇" : "🔊");
    public string MuteTip => (IsMuted ? "Show " : "Hide ") + label;

    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand ToggleMuteCommand { get; set; } = null!;
}

public sealed class TakeRow : ObservableObject
{
    public TakeRow(Take take, int number, bool isFavorite,
        Action<TakeRow> toggleFavorite, Action<TakeRow> delete)
    {
        Take = take;
        Number = number;
        IsFavorite = isFavorite;
        ToggleFavoriteCommand = new RelayCommand(() => toggleFavorite(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public Take Take { get; }
    public int Number { get; }
    public bool IsFavorite { get; }

    public string DisplayName => $"Take {Number:D2}";
    public string StarText => IsFavorite ? "★" : "☆";
    public IReadOnlyList<string> Badges => Take.Sources.Select(s => s.Kind.Badge()).ToList();
    public string Duration => TimeSpan.FromSeconds(Take.DurationSeconds).ToString(@"m\:ss");

    public ICommand ToggleFavoriteCommand { get; }
    public ICommand DeleteCommand { get; }
}

public sealed class MainViewModel : ObservableObject
{
    private ProjectStore? _store;

    public ObservableCollection<SceneViewModel> Scenes { get; } = new();
    public ObservableCollection<TakeRow> Takes { get; } = new();
    public ObservableCollection<PaletteSourceViewModel> PaletteSources { get; } = new();
    public ObservableCollection<SceneSourceViewModel> SceneSources { get; } = new();

    private SceneViewModel? _selectedScene;
    public SceneViewModel? SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (SetField(ref _selectedScene, value))
            {
                RefreshSceneDetails();
                Raise(nameof(NextTakeText));
                Raise(nameof(SceneHeaderText));
                if (_playerTake is not null && _playerSceneId != value?.Scene.Id)
                    _ = UnloadPlayerAsync();
            }
        }
    }

    public string SceneHeaderText => SelectedScene?.SceneHeader ?? "Scene";

    private TakeRow? _selectedTake;
    public TakeRow? SelectedTake
    {
        get => _selectedTake;
        set
        {
            var oldId = _selectedTake?.Take.Id;
            if (SetField(ref _selectedTake, value))
            {
                Raise(nameof(IsFavorite));
                if (value is not null && value.Take.Id != oldId)
                    LoadPlayerTake(value.Take);
                else if (value is null)
                    _ = UnloadPlayerAsync();
            }
        }
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
    public ICommand AddPaletteSourceCommand { get; }
    public ICommand AddSceneSourceCommand { get; }

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
        _playerTimer.Tick += (_, _) => UpdatePlayerState();
        PlayerPlayPauseCommand = new RelayCommand(TogglePlayerPlayPauseAsync,
            () => _player is not null && !IsRecording);
        ExportCommand = new RelayCommand(ShowExportDialog,
            () => HasProject && !IsRecording);
        AddPaletteSourceCommand = new RelayCommand(AddPaletteSourceAsync, () => HasProject && !IsRecording);
        AddSceneSourceCommand = new RelayCommand(AddSceneSource,
            () => HasProject && !IsRecording && SelectedScene is not null && SelectedPaletteSourceToAdd is not null);

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
                                  ToggleRecordCommand, NewProjectCommand, OpenProjectCommand,
                                  AddPaletteSourceCommand, AddSceneSourceCommand,
                                  PlayerPlayPauseCommand, ExportCommand }
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
        _ = UnloadPlayerAsync();
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
        {
            var vm = new SceneViewModel(s, () => _store.SaveScene(s));
            vm.TakeCount = _store.LoadTakes(s.Id).Count;
            Scenes.Add(vm);
        }
        SelectedScene = Scenes.FirstOrDefault();
        foreach (var vm in Scenes) vm.RefreshOrder();
        ReloadPalette();
    }

    private void ReloadPalette()
    {
        PaletteSources.Clear();
        if (_store is null) return;
        foreach (var p in _store.Project.Sources)
        {
            var vm = new PaletteSourceViewModel(p);
            vm.DeleteCommand = new RelayCommand(() => Mutate(() => DeletePaletteSource(vm)));
            PaletteSources.Add(vm);
        }
        Raise(nameof(AvailablePaletteSources));
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

    // ----- takes + scene sources -----

    private void RefreshSceneDetails()
    {
        RefreshTakes();
        RefreshSceneSources();
    }

    private void RefreshTakes(Take? selectTake = null)
    {
        var selectedId = selectTake?.Id ?? SelectedTake?.Take.Id;
        // Don't let the refresh itself unload the player; only a real
        // selection change does that.
        _selectedTake = null;
        Takes.Clear();
        Raise(nameof(SelectedTake));
        if (_store is null || SelectedScene is null) return;
        var fav = SelectedScene.Scene.FavoriteTakeId;
        var n = 1;
        foreach (var t in _store.LoadTakes(SelectedScene.Scene.Id))
            Takes.Add(new TakeRow(t, n++, t.Id == fav, ToggleFavoriteRow, DeleteTakeRow));
        SelectedScene.TakeCount = Takes.Count;
        SelectedTake = Takes.FirstOrDefault(t => t.Take.Id == selectedId);
        Raise(nameof(NextTakeText));
    }

    private void RefreshSceneSources()
    {
        SceneSources.Clear();
        _selectedPaletteSourceToAdd = null;
        if (_store is null || SelectedScene is null) return;
        foreach (var ss in SelectedScene.Scene.Sources)
        {
            var palette = _store.Project.Sources.FirstOrDefault(p => p.Id == ss.SourceId);
            if (palette is null) continue; // dangling ref; cleaned on next save
            var vm = new SceneSourceViewModel(ss, palette);
            vm.ToggleCommand = new RelayCommand(() => Mutate(() =>
            {
                ss.Enabled = !ss.Enabled;
                _store.SaveScene(SelectedScene.Scene);
                vm.Refresh();
            }));
            vm.RemoveCommand = new RelayCommand(() => Mutate(() =>
            {
                SelectedScene.Scene.Sources.Remove(ss);
                _store.SaveScene(SelectedScene.Scene);
                RefreshSceneSources();
            }));
            SceneSources.Add(vm);
        }
        Raise(nameof(AvailablePaletteSources));
        Raise(nameof(NextTakeText));
        ((RelayCommand)AddSceneSourceCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Palette entries not yet assigned to the selected scene.</summary>
    public IReadOnlyList<PaletteSourceViewModel> AvailablePaletteSources =>
        _store is null || SelectedScene is null
            ? []
            : PaletteSources
                .Where(p => SelectedScene.Scene.Sources.All(s => s.SourceId != p.Source.Id))
                .ToList();

    private PaletteSourceViewModel? _selectedPaletteSourceToAdd;
    public PaletteSourceViewModel? SelectedPaletteSourceToAdd
    {
        get => _selectedPaletteSourceToAdd;
        set
        {
            SetField(ref _selectedPaletteSourceToAdd, value);
            ((RelayCommand)AddSceneSourceCommand).RaiseCanExecuteChanged();
        }
    }

    private void AddSceneSource()
    {
        if (_store is null || SelectedScene is null || SelectedPaletteSourceToAdd is null) return;
        var palette = SelectedPaletteSourceToAdd.Source;
        // New video sources land as a PiP; audio is full-frame (ignored for audio).
        var videoCount = SelectedScene.Scene.Sources.Count(s =>
            _store.Project.Sources.FirstOrDefault(p => p.Id == s.SourceId)?.Kind.IsVideo() == true);
        var layout = palette.Kind.IsVideo()
            ? (0.72, 0.70 - 0.28 * (videoCount % 3), 0.26, 0.26)
            : (0.0, 0.0, 1.0, 1.0);
        SelectedScene.Scene.Sources.Add(new SceneSource
        {
            SourceId = palette.Id,
            X = layout.Item1, Y = layout.Item2, Width = layout.Item3, Height = layout.Item4,
        });
        _store.SaveScene(SelectedScene.Scene);
        Mutate(RefreshSceneSources);
    }

    public string NextTakeText
    {
        get
        {
            if (SelectedScene is null) return "";
            var on = SelectedScene.Scene.Sources.Count(s => s.Enabled);
            return $"Next: Take {Takes.Count + 1:D2} · {on} source{(on == 1 ? "" : "s")} on";
        }
    }

    private async void DeleteTakeRow(TakeRow row)
    {
        if (_store is null || SelectedScene is null) return;
        if (_playerTake?.Id == row.Take.Id)
            await UnloadPlayerAsync();
        var label = row.DisplayName;
        _store.DeleteTake(SelectedScene.Scene.Id, row.Take);
        RefreshTakes();
        Status = $"Deleted {label}";
    }

    private void ToggleFavoriteRow(TakeRow row)
    {
        if (_store is null || SelectedScene is null) return;
        var scene = SelectedScene.Scene;
        var id = row.Take.Id;
        scene.FavoriteTakeId = scene.FavoriteTakeId == id ? null : id;
        _store.SaveScene(scene);
        RefreshTakes();
    }

    private void DeleteTake()
    {
        if (SelectedTake is not null) Mutate(() => DeleteTakeRow(SelectedTake));
    }

    private void ToggleFavorite()
    {
        if (SelectedTake is not null) Mutate(() => ToggleFavoriteRow(SelectedTake));
    }

    // ----- palette -----

    private async void AddPaletteSourceAsync()
    {
        if (_store is null) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var dialog = new AddSourceWindow();
        var result = await dialog.ShowDialog<ProjectSource?>(desktop.MainWindow!);
        if (result is null) return;
        _store.Project.Sources.Add(result);
        _store.SaveProject();
        Mutate(ReloadPalette);
        Status = $"Added source \"{result.Label}\"";
    }

    private void DeletePaletteSource(PaletteSourceViewModel vm)
    {
        if (_store is null) return;
        var id = vm.Source.Id;
        _store.Project.Sources.RemoveAll(p => p.Id == id);
        foreach (var scene in _store.LoadScenes())
        {
            var removed = scene.Sources.RemoveAll(s => s.SourceId == id);
            if (removed > 0) _store.SaveScene(scene);
        }
        _store.SaveProject();
        ReloadPalette();
        RefreshSceneSources();
        Status = $"Deleted source \"{vm.Label}\"";
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
                RefreshTakes(take);
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
            // Don't let the preview's audio bleed into the new take's mic.
            if (_player is not null)
            {
                try { await _player.PauseAsync(); } catch { }
            }
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
        foreach (var c in new[] { ToggleRecordCommand, NewProjectCommand, OpenProjectCommand,
                                  PlayerPlayPauseCommand, ExportCommand }
                 .OfType<RelayCommand>())
            c.RaiseCanExecuteChanged();
    }

    // ----- player -----

    private MpvPlaybackEngine? _player;
    private Take? _playerTake;
    private string? _playerSceneId;
    private string? _playerError;
    private bool _playerScrubbing;
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public ObservableCollection<PlayerTrackRow> PlayerTracks { get; } = new();

    /// <summary>Native child-window handle from MpvVideoHost. 0 = not embedded.</summary>
    private nint _videoHandle;
    public nint VideoHandle
    {
        get => _videoHandle;
        set
        {
            if (_videoHandle == value) return;
            _videoHandle = value;
            if (value == nint.Zero)
                _ = UnloadPlayerAsync();
            else if (_playerTake is not null && _player is null)
                LoadPlayerTake(_playerTake);
        }
    }

    public bool HasPlayerTake => _playerTake is not null;
    public string PlayerPlaceholderText =>
        _playerError ?? "Select a take to preview";

    private double _playerPositionSeconds;
    public double PlayerPositionSeconds
    {
        get => _playerPositionSeconds;
        private set
        {
            if (SetField(ref _playerPositionSeconds, value))
                Raise(nameof(PlayerTimeText));
        }
    }

    private double _playerDurationSeconds;
    public double PlayerDurationSeconds
    {
        get => _playerDurationSeconds;
        private set
        {
            if (SetField(ref _playerDurationSeconds, value))
                Raise(nameof(PlayerTimeText));
        }
    }

    public string PlayerTimeText =>
        $"{TimeSpan.FromSeconds(_playerPositionSeconds):m\\:ss} / {TimeSpan.FromSeconds(_playerDurationSeconds):m\\:ss}";

    private bool _isPlayerPlaying;
    public bool IsPlayerPlaying
    {
        get => _isPlayerPlaying;
        private set
        {
            if (SetField(ref _isPlayerPlaying, value))
                Raise(nameof(PlayerPlayPauseText));
        }
    }

    public string PlayerPlayPauseText => IsPlayerPlaying ? "⏸ Pause" : "▶ Play";
    public ICommand PlayerPlayPauseCommand { get; }
    public ICommand ExportCommand { get; }

    private async void LoadPlayerTake(Take take)
    {
        if (_store is null || IsRecording) return;
        if (_playerTake?.Id == take.Id && _player is not null) return;

        // Keep continuations on the UI thread: everything below raises
        // property notifications bound to the UI.
        await UnloadPlayerAsync();
        var sceneId = SelectedScene?.Scene.Id;
        if (sceneId is null) return;

        _playerTake = take;
        _playerSceneId = sceneId;
        _playerError = null;
        Raise(nameof(HasPlayerTake));
        Raise(nameof(PlayerPlaceholderText));

        try
        {
            var scene = SelectedScene!.Scene;
            var takeDir = _store.TakeDir(sceneId, take.Id);
            try
            {
                await StartPlayerEngineAsync(scene, takeDir, take, _videoHandle);
            }
            catch when (_videoHandle != nint.Zero)
            {
                // Embedding failed (e.g. unexpected platform handle): fall back
                // to mpv's own window rather than leaving the take unplayable.
                await StartPlayerEngineAsync(scene, takeDir, take, nint.Zero);
                Status = "Video embedding isn't working on this machine — mpv opened its own window.";
            }

            PlayerTracks.Clear();
            foreach (var ts in take.Sources)
            {
                var label = _store.Project.Sources.FirstOrDefault(p => p.Id == ts.Id)?.Label
                    ?? ts.Kind.DefaultLabel();
                var row = new PlayerTrackRow(ts, label);
                row.ToggleMuteCommand = new RelayCommand(() => ToggleTrackMuteAsync(row));
                PlayerTracks.Add(row);
            }
            _playerTimer.Start();
            ((RelayCommand)PlayerPlayPauseCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _playerError = ex.Message;
            await UnloadPlayerAsync();
            Raise(nameof(PlayerPlaceholderText));
        }
    }

    private async Task StartPlayerEngineAsync(Scene scene, string takeDir, Take take, nint handle)
    {
        var engine = new MpvPlaybackEngine { EmbedWindowHandle = handle };
        try
        {
            engine.PlaybackEnded += OnPlayerEnded;
            await engine.LoadAsync(_store!.Project, scene, takeDir, take).ConfigureAwait(false);
            _player = engine;
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnPlayerEnded()
    {
        // mpv hit end-of-file: rewind and pause for instant replay.
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (_player is null) return;
            try
            {
                await _player.SeekAsync(TimeSpan.Zero);
                await _player.PauseAsync();
            }
            catch { /* player is going away */ }
        });
    }

    private async Task UnloadPlayerAsync()
    {
        _playerTimer?.Stop();
        PlayerTracks.Clear();
        var player = _player;
        _player = null;
        _playerTake = null;
        _playerSceneId = null;
        if (player is not null)
            await player.DisposeAsync().ConfigureAwait(false);
        PlayerPositionSeconds = 0;
        PlayerDurationSeconds = 0;
        IsPlayerPlaying = false;
        Raise(nameof(HasPlayerTake));
        if (_playerError is null) Raise(nameof(PlayerPlaceholderText));
        if (PlayerPlayPauseCommand is not null)
            ((RelayCommand)PlayerPlayPauseCommand).RaiseCanExecuteChanged();
    }

    private void UpdatePlayerState()
    {
        var player = _player;
        if (player is null) return;
        IsPlayerPlaying = player.IsPlaying;
        PlayerDurationSeconds = player.Duration.TotalSeconds;
        if (!_playerScrubbing)
            PlayerPositionSeconds = player.Position.TotalSeconds;
    }

    private async void TogglePlayerPlayPauseAsync()
    {
        if (_player is null || IsRecording) return;
        try
        {
            if (_player.IsPlaying) await _player.PauseAsync();
            else await _player.PlayAsync();
        }
        catch (Exception ex) { Status = $"Player: {ex.Message}"; }
    }

    public void BeginScrub() => _playerScrubbing = true;

    public async void EndScrub(double seconds)
    {
        _playerScrubbing = false;
        if (_player is null) return;
        try
        {
            await _player.SeekAsync(TimeSpan.FromSeconds(seconds));
            PlayerPositionSeconds = seconds;
        }
        catch (Exception ex) { Status = $"Seek: {ex.Message}"; }
    }

    private async void ToggleTrackMuteAsync(PlayerTrackRow row)
    {
        if (_player is null) return;
        var muting = !row.IsMuted;
        if (muting && PlayerTracks.All(t => t == row || t.IsMuted))
        {
            Status = "Can't mute every track in the take.";
            return;
        }
        try
        {
            await _player.SetTrackMutedAsync(row.SourceId, muting);
            row.IsMuted = muting;
        }
        catch (Exception ex) { Status = $"Mute: {ex.Message}"; }
    }

    // ----- export (shell for M4) -----

    private void ShowExportDialog()
    {
        if (_store is null) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var win = new ExportWindow();
        var lines = new List<string>();
        foreach (var scene in _store.LoadScenes())
        {
            var takes = _store.LoadTakes(scene.Id).ToList();
            var favIndex = takes.FindIndex(t => t.Id == scene.FavoriteTakeId);
            var favText = favIndex < 0
                ? "no favorite take"
                : $"★ Take {favIndex + 1:D2}";
            lines.Add($"{scene.Order + 1:D2} {scene.Title} — {favText}");
        }
        win.SetScenes(lines);
        win.ShowDialog(desktop.MainWindow!);
    }
}
