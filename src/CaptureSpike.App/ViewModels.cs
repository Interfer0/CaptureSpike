using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Avalonia.Controls;
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

public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(Wrap(execute), canExecute) { }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? _) => _canExecute?.Invoke() ?? true;
    public void Execute(object? _) => _ = RunAsync();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync()
    {
        try { await _execute().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.ReportCrash(ex);
        }
    }

    private static Func<Task> Wrap(Action execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return () => { execute(); return Task.CompletedTask; };
    }
}

internal static class AppSettings
{
    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CaptureSpike", "settings.json");

    /// <summary>Where the settings JSON lives. Exposed so the App startup
    /// can detect first-run (file does not exist yet) without depending
    /// on internal helpers.</summary>
    public static string SettingsFilePath => Path_;

    private sealed class SettingsData
    {
        [JsonPropertyName("lastProjectDir")]
        public string? LastProjectDir { get; set; }
        [JsonPropertyName("projectsDir")]
        public string? ProjectsDir { get; set; }
        [JsonPropertyName("theme")]
        /// <summary>"Dark" | "Light" | "Default" (follow OS). Defaults to "Dark"
        /// for first-run users; existing settings.json files (no theme field)
        /// continue to follow the OS until the user picks one.</summary>
        public string? Theme { get; set; }
        [JsonPropertyName("windowX")]
        public double? WindowX { get; set; }
        [JsonPropertyName("windowY")]
        public double? WindowY { get; set; }
        [JsonPropertyName("windowWidth")]
        public double? WindowWidth { get; set; }
        [JsonPropertyName("windowHeight")]
        public double? WindowHeight { get; set; }
        [JsonPropertyName("windowMaximized")]
        public bool WindowMaximized { get; set; }
    }

    private static SettingsData Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(Path_))
                    ?? new SettingsData();
        }
        catch { }
        return new SettingsData();
    }

    private static void Save(SettingsData data)
    {
        try { AtomicFile.WriteAllText(Path_, JsonSerializer.Serialize(data)); }
        catch { /* settings are best-effort */ }
    }

    public static string? LastProjectDir
    {
        get => Load().LastProjectDir;
        set { var d = Load(); d.LastProjectDir = value; Save(d); }
    }

    public sealed record WindowBounds(double X, double Y, double Width, double Height, bool Maximized);

    public static WindowBounds? LoadWindowBounds()
    {
        var d = Load();
        if (d.WindowX is null || d.WindowY is null ||
            d.WindowWidth is null || d.WindowHeight is null)
            return null;
        return new WindowBounds(d.WindowX.Value, d.WindowY.Value,
            d.WindowWidth.Value, d.WindowHeight.Value, d.WindowMaximized);
    }

    public static void SaveWindowBounds(double x, double y, double width, double height, bool maximized)
    {
        var d = Load();
        d.WindowX = x; d.WindowY = y;
        d.WindowWidth = width; d.WindowHeight = height;
        d.WindowMaximized = maximized;
        Save(d);
    }

    /// <summary>Library folder containing one folder per project.</summary>
    public static string ProjectsDir
    {
        get => Load().ProjectsDir ?? DefaultProjectsDir();
        set { var d = Load(); d.ProjectsDir = value; Save(d); }
    }

    /// <summary>
    /// UI theme: "Dark", "Light", or "Default" (follow OS). Returns the
    /// stored value, or "Dark" for first-run users who have never picked one.
    /// Returns null only for pre-existing settings files where the theme
    /// field is missing — those users keep the OS-driven default until they
    /// open Settings and pick something.
    /// </summary>
    public static string? Theme
    {
        get => Load().Theme;
        set { var d = Load(); d.Theme = value; Save(d); }
    }

    public static string DefaultProjectsDir()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
            videos = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        return Path.Combine(videos, "CaptureSpike");
    }
}

#endregion

public sealed class SceneViewModel(ProjectStore store, Scene scene) : ObservableObject
{
    public Scene Scene { get; } = scene;

    public string Title
    {
        get => Scene.Title;
        set
        {
            if (Scene.Title == value) return;
            // Renames the scene's folder on disk too; if that failed the
            // title stays as it was.
            store.RenameScene(Scene, value);
            Raise();
            Raise(nameof(DisplayName));
        }
    }

    public string Notes
    {
        get => Scene.Notes;
        set { Scene.Notes = value; store.SaveScene(Scene); Raise(); Raise(nameof(WordCountText)); }
    }

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

public sealed class PaletteSourceViewModel : ObservableObject
{
    private static readonly DeviceOption DefaultOption = new("", "Default");

    private readonly Action _save;
    private readonly Action _onDeviceChanged;

    public PaletteSourceViewModel(ProjectSource source, Action save, Action onDeviceChanged)
    {
        Source = source;
        _save = save;
        _onDeviceChanged = onDeviceChanged;
        Devices.Add(DefaultOption);
        _selectedDevice = DefaultOption;
        RefreshDevicesCommand = new RelayCommand(
            async () => await RefreshDevicesAsync(), () => !IsDetecting);
    }

    public ProjectSource Source { get; }
    public string Badge
    {
        get => Source.EffectiveBadge();
        set
        {
            var b = (value ?? "").Trim();
            if (Source.Badge == b) return;
            Source.Badge = b;
            // Badge changes are persisted as a per-project override
            // (SourceOverride.Badge) so the user's badge choice stays
            // attached to this project. The same source can have a
            // different badge in another project.
            _save();
            Raise(nameof(Badge));
            OnBadgeChanged();
        }
    }
    /// <summary>Assigned by MainViewModel after construction.</summary>
    public Action OnBadgeChanged { get; set; } = () => { };
    public string Label => Source.Label;
    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand DeleteCommand { get; set; } = null!;

    // ----- device picker -----

    public ObservableCollection<DeviceOption> Devices { get; } = new();

    private DeviceOption? _selectedDevice;
    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetField(ref _selectedDevice, value))
            {
                Source.Device = string.IsNullOrEmpty(value?.Id) ? null : value.Id;
                _save();
                Raise(nameof(DeviceDisplay));
                Raise(nameof(IsDeviceMissing));
                _onDeviceChanged();
            }
        }
    }

    public string DeviceDisplay =>
        !DetectionCompleted ? "Detecting devices…"
        : SelectedDevice is null ? "Default"
        : SelectedDevice.Label;

    private bool _detecting;
    public bool IsDetecting
    {
        get => _detecting;
        private set
        {
            if (SetField(ref _detecting, value))
            {
                Raise(nameof(RefreshGlyph));
                (RefreshDevicesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    public string RefreshGlyph => IsDetecting ? "…" : "↻";
    public ICommand RefreshDevicesCommand { get; }

    // Has detection ever completed for this VM? Until it has, we can't
    // claim a device is missing — maybe detection is just slow or the
    // user hasn't clicked "Refresh" yet. The Record-gating logic
    // ignores "indeterminate" sources.
    private bool _detectionCompleted;
    public bool DetectionCompleted
    {
        get => _detectionCompleted;
        private set
        {
            if (SetField(ref _detectionCompleted, value))
                Raise(nameof(IsDeviceMissing));
        }
    }

    /// <summary>
    /// True when the user has a specific device selected (Source.Device is
    /// non-empty) but that device wasn't seen in the latest detection
    /// sweep. Drives a red border on the source row and disables Record.
    /// </summary>
    public bool IsDeviceMissing
    {
        get
        {
            if (!DetectionCompleted) return false;
            if (string.IsNullOrEmpty(Source.Device)) return false;
            // The "Default" entry's Id is ""; anything else is a real
            // device choice. If SelectedDevice landed on the (not
            // connected) sentinel, the source device is missing.
            if (SelectedDevice is null) return true;
            return SelectedDevice.Label.EndsWith("(not connected)",
                StringComparison.Ordinal);
        }
    }

    /// <summary>Detects real devices in the background; never throws.</summary>
    public async Task RefreshDevicesAsync()
    {
        if (IsDetecting) return;
        IsDetecting = true;
        try
        {
            string ffmpeg;
            try { ffmpeg = ToolPaths.Resolve("ffmpeg"); }
            catch { ffmpeg = "ffmpeg"; } // let the OS resolve it; failures -> empty list
            var kind = Source.Kind;
            var found = await Task.Run(() => DeviceEnumeration.GetDevices(kind, ffmpeg));
            Devices.Clear();
            Devices.Add(DefaultOption);
            foreach (var d in found) Devices.Add(d);
            var current = Source.Device;
            var match = Devices.FirstOrDefault(d => d.Id == current);
            if (match is null && !string.IsNullOrEmpty(current))
            {
                match = new DeviceOption(current, $"{current} (not connected)");
                Devices.Add(match);
            }
            _selectedDevice = match ?? DefaultOption;
            Raise(nameof(SelectedDevice));
            Raise(nameof(DeviceDisplay));
            DetectionCompleted = true;
        }
        catch
        {
            // Detection is best-effort. We DO mark DetectionCompleted =
            // true here so the dropdown doesn't get stuck on
            // 'Detecting devices…' forever — the user would think
            // detection is still running and that the saved device
            // isn't available. The "missing device" red flag is
            // driven by IsDeviceMissing, which is independently
            // false here (we don't know whether the device is
            // missing — we just couldn't enumerate at all).
            DetectionCompleted = true;
            Raise(nameof(DeviceDisplay));
        }
        finally
        {
            IsDetecting = false;
        }
    }
}

public sealed class SceneSourceViewModel(SceneSource sceneSource, ProjectSource palette)
    : ObservableObject
{
    public SceneSource SceneSource { get; } = sceneSource;
    public string Badge => palette.EffectiveBadge();
    public string Label => palette.Label;
    public bool IsEnabled => SceneSource.Enabled;
    public string Dot => SceneSource.Enabled ? "🟢" : "⚪";

    private PaletteSourceViewModel? _paletteVm;
    public void SetPaletteSource(PaletteSourceViewModel? vm) => _paletteVm = vm;
    public string DeviceLabel => _paletteVm?.DeviceDisplay ?? "Default";
    public void RaiseDeviceLabel() => Raise(nameof(DeviceLabel));

    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand ToggleCommand { get; set; } = null!;
    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand RemoveCommand { get; set; } = null!;

    public void Refresh() { Raise(nameof(IsEnabled)); Raise(nameof(Dot)); }
}

/// <summary>A source-kind chip that filters the takes list.</summary>
public sealed class TakeFilterChip(SourceKind kind) : ObservableObject
{
    public SourceKind Kind => kind;
    public string Badge => kind.Badge();

    private bool _active;
    public bool IsActive
    {
        get => _active;
        set
        {
            if (SetField(ref _active, value))
                Raise(nameof(ChipOpacity));
        }
    }

    public double ChipOpacity => IsActive ? 1.0 : 0.35;

    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand ToggleCommand { get; set; } = null!;
}

public sealed class PlayerTrackRow(TakeSource source, string label, string badge) : ObservableObject
{
    public TakeSource Source { get; } = source;
    public string SourceId => Source.Id;
    public string Badge => badge;
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

    private bool _isIncluded = true;
    /// <summary>Whether this source is included in multi-window playback.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (SetField(ref _isIncluded, value))
            {
                Raise(nameof(IncludeIcon));
                Raise(nameof(IncludeTip));
            }
        }
    }

    public string IncludeIcon => IsIncluded ? "👁" : "🚫";
    public string IncludeTip => (IsIncluded ? "Exclude " : "Include ") + label + " from playback";

    /// <summary>Assigned by MainViewModel after construction.</summary>
    public ICommand ToggleMuteCommand { get; set; } = null!;
    /// <summary>Assigned by MainViewModel after construction (multi-window only).</summary>
    public ICommand ToggleIncludeCommand { get; set; } = null!;
}

public sealed class TakeFileRow : ObservableObject
{
    private readonly Action _onOffsetChanged;

    public TakeFileRow(string fullPath, long bytes, string? sourceLabel,
        TakeSource? source, Action onOffsetChanged)
    {
        FullPath = fullPath;
        Bytes = bytes;
        FileName = Path.GetFileName(fullPath)!;
        SourceLabel = sourceLabel;
        Source = source;
        _onOffsetChanged = onOffsetChanged;
        ResetOffsetCommand = new RelayCommand(ResetOffset, () => Source?.ManualOffsetMs is not null);
    }

    public string FullPath { get; }
    public string FileName { get; }
    /// <summary>Palette label of the source that produced this file (null for take.json).</summary>
    public string? SourceLabel { get; }
    /// <summary>The take.json source entry for this file (null if not matched).</summary>
    public TakeSource? Source { get; }
    public string DisplayName =>
        SourceLabel is not null ? $"{SourceLabel} — {FileName}" : FileName;
    public string SizeText => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:F1} KB",
        _ => $"{Bytes / (1024.0 * 1024):F1} MB",
    };
    private long Bytes { get; }

    /// <summary>Recorded offset (ms) — never edited, for reference.</summary>
    public string RecordedOffsetText =>
        Source is null ? "" : $"recorded: {Source.OffsetMs:F0}ms";

    /// <summary>
    /// Editable offset as text. Shows the manual value if set, else the recorded
    /// value. Setting it parses and makes it manual (saves take.json).
    /// </summary>
    public string EditableOffsetText
    {
        get => (Source?.ManualOffsetMs ?? Source?.OffsetMs ?? 0).ToString("F0");
        set
        {
            if (Source is null) return;
            if (!double.TryParse(value, out var parsed)) return;
            // Only mark as manual if it differs from the recorded value.
            var newManual = Math.Abs(parsed - Source.OffsetMs) < 0.5 ? null : (double?)parsed;
            if (Source.ManualOffsetMs == newManual) return;
            Source.ManualOffsetMs = newManual;
            _onOffsetChanged();
            Raise(nameof(EditableOffsetText));
            Raise(nameof(OffsetSummaryText));
            ((RelayCommand)ResetOffsetCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>Manual offset in ms (null = use recorded). Kept for binding compat.</summary>
    public double? ManualOffsetMs
    {
        get => Source?.ManualOffsetMs;
        set
        {
            if (Source is null || Source.ManualOffsetMs == value) return;
            Source.ManualOffsetMs = value;
            _onOffsetChanged();
            Raise(nameof(ManualOffsetMs));
            Raise(nameof(EditableOffsetText));
            Raise(nameof(OffsetSummaryText));
            ((RelayCommand)ResetOffsetCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>Shows effective offset, flagging manual overrides.</summary>
    public string OffsetSummaryText =>
        Source is null ? "" :
        Source.ManualOffsetMs is not null
            ? $"offset: {Source.ManualOffsetMs:F0}ms (manual)"
            : $"offset: {Source.OffsetMs:F0}ms";

    public System.Windows.Input.ICommand ResetOffsetCommand { get; }

    private void ResetOffset()
    {
        if (Source is null) return;
        Source.ManualOffsetMs = null;
        _onOffsetChanged();
        Raise(nameof(ManualOffsetMs));
        Raise(nameof(EditableOffsetText));
        Raise(nameof(OffsetSummaryText));
        ((RelayCommand)ResetOffsetCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Whether offset controls are shown (only for matched source files).</summary>
    public bool HasOffset => Source is not null;
}

public sealed class TakeRow : ObservableObject
{
    public TakeRow(Take take, int number, bool isFavorite, string folderPath,
        Action<TakeRow> toggleFavorite, Action<TakeRow> delete, Action<TakeRow> openFolder,
        Action<TakeRow> normalize,
        Func<string, string?> sourceLabel, Func<string, string?> sourceBadge,
        Action<TakeRow> playTake, Action<TakeRow> saveTake)
    {
        Take = take;
        Number = number;
        IsFavorite = isFavorite;
        FolderPath = folderPath;
        ToggleFavoriteCommand = new RelayCommand(() => toggleFavorite(this));
        DeleteCommand = new RelayCommand(() => delete(this));
        OpenFolderCommand = new RelayCommand(() => openFolder(this));
        NormalizeCommand = new RelayCommand(() => normalize(this));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        PlayTakeCommand = new RelayCommand(() => playTake(this));
        _sourceLabel = sourceLabel;
        _sourceBadge = sourceBadge;
        _saveTake = saveTake;
    }

    public Take Take { get; }
    public int Number { get; }
    public bool IsFavorite { get; }

    /// <summary>Stable per-scene number; falls back to position for legacy takes.</summary>
    public int DisplayNumber => Take.Number > 0 ? Take.Number : Number;
    public string DisplayName => $"Take {DisplayNumber:D2}";
    public string StarText => IsFavorite ? "★" : "☆";
    public IReadOnlyList<string> Badges => Take.Sources
        .Select(s => _sourceBadge(s.Id) ?? s.Kind.Badge()).ToList();
    public string Duration => TimeSpan.FromSeconds(Take.DurationSeconds).ToString(@"m\:ss");

    public ICommand ToggleFavoriteCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand NormalizeCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand PlayTakeCommand { get; }

    /// <summary>Take folder on disk; listed when the row is expanded.</summary>
    public string FolderPath { get; }
    public ObservableCollection<TakeFileRow> Files { get; } = new();

    private readonly Func<string, string?> _sourceLabel;
    private readonly Func<string, string?> _sourceBadge;
    private readonly Action<TakeRow> _saveTake;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                Raise(nameof(ExpandGlyph));
                if (value) LoadFiles();
            }
        }
    }
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    private void LoadFiles()
    {
        if (Files.Count > 0 || string.IsNullOrEmpty(FolderPath)) return;
        try
        {
            foreach (var f in Directory.GetFiles(FolderPath).OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(f)!;
                // take.json is metadata, not a playable source file.
                if (name.Equals("take.json", StringComparison.OrdinalIgnoreCase)) continue;
                var ts = Take.Sources.FirstOrDefault(s =>
                    s.File.Equals(name, StringComparison.OrdinalIgnoreCase));
                var label = ts is null ? null : _sourceLabel(ts.Id);
                Files.Add(new TakeFileRow(f, new FileInfo(f).Length, label, ts,
                    () => _saveTake(this)));
            }
        }
        catch
        {
            // Folder may vanish if deleted externally; the row just stays empty.
        }
    }
}

public sealed class MainViewModel : ObservableObject, ITransportHost
{
    private ProjectStore? _store;
    /// <summary>
    /// Global palette loaded once at app start. The Sources panel
    /// reads from this; AddPaletteSource / DeletePaletteSource mutate
    /// it and Save() to <see cref="GlobalPalette.FilePath"/>.
    /// </summary>
    private GlobalPalette _palette = GlobalPalette.Load();

    public ObservableCollection<SceneViewModel> Scenes { get; } = new();
    public ObservableCollection<TakeRow> Takes { get; } = new();
    public ObservableCollection<PaletteSourceViewModel> PaletteSources { get; } = new();
    public ObservableCollection<SceneSourceViewModel> SceneSources { get; } = new();
    public ObservableCollection<TakeFilterChip> TakeFilterChips { get; } = new();

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
                Raise(nameof(SceneNumberText));
                Raise(nameof(TakeSceneHeader));
                RaiseCommandStates();
                // Re-evaluate which palette sources are missing — the
                // blocking status is per-scene (a source only matters
                // if the current scene actually includes it).
                RecomputeBlockingDeviceIssue();
                if (_playerTake is not null && _playerSceneId != value?.Scene.Id)
                    _ = UnloadPlayerAsync();
            }
        }
    }

    public string SceneNumberText =>
        SelectedScene is null ? "" : $"Scene {SelectedScene.Scene.Order + 1:D2}";
    public string TakeSceneHeader =>
        SelectedScene is null ? "" : $"Scene {SelectedScene.Scene.Order + 1:D2} - {SelectedScene.Title}";
    public string TakeHeaderText => SelectedTake?.DisplayName ?? "No take selected";

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
                Raise(nameof(TakeHeaderText));
                // Selection no longer auto-plays; use the take's play button
                // for multi-window playback.
                if (value is null)
                    _ = UnloadPlayerAsync();
            }
        }
    }

    private string _status = "No project open.";
    public string Status
    {
        get => _status;
        set
        {
            // Thread-safe: Status is set from worker threads (e.g., after
            // ConfigureAwait(false)); marshal to the UI thread.
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = value);
                return;
            }
            if (SetField(ref _status, value))
                Log(value);
        }
    }

    // ----- Log output panel -----

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Path to the session log on disk — same path as the in-app panel.</summary>
    public string LogFilePath => AppLog.LogDirectory;

    /// <summary>
    /// Appends a timestamped line to the log panel (capped at 500) and
    /// to the on-disk session log (AppLog) so the user has a durable copy
    /// they can find without re-launching the app.
    /// Thread-safe: marshals to the UI thread if called from a worker.
    /// </summary>
    public void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(message));
            return;
        }
        var stamped = $"{DateTime.Now:HH:mm:ss} {message}";
        LogLines.Add(stamped);
        while (LogLines.Count > 500) LogLines.RemoveAt(0);
        // Mirror to disk so the user can grab the log even if the app dies.
        AppLog.Open().WriteLine(message);
    }

    /// <summary>
    /// Returns the current log as a single multi-line string (no trailing
    /// newline). Pure function on <see cref="LogLines"/>; the join logic
    /// lives in <see cref="CaptureSpike.Core.LogText"/> so it can be
    /// exercised by Core.Tests without Avalonia.
    /// </summary>
    public string BuildLogText() => CaptureSpike.Core.LogText.Join(LogLines);

    public ICommand ClearLogCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    private readonly IClipboardWriter _clipboard;

    private bool _isLogVisible = true;
    public bool IsLogVisible
    {
        get => _isLogVisible;
        set => SetField(ref _isLogVisible, value);
    }

    private bool _isSourcesVisible = true;
    public bool IsSourcesVisible
    {
        get => _isSourcesVisible;
        set
        {
            if (SetField(ref _isSourcesVisible, value))
            {
                Raise(nameof(SourcesRowHeight));
                Raise(nameof(SourcesSplitterHeight));
            }
        }
    }

    /// <summary>Grid row height for the Sources panel (collapses to 0 when hidden).</summary>
    public GridLength SourcesRowHeight =>
        IsSourcesVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    /// <summary>Grid row height for the spacer above Sources (collapses when hidden).</summary>
    public GridLength SourcesSplitterHeight =>
        IsSourcesVisible ? new GridLength(8) : new GridLength(0);

    private string _projectName = "";
    public string ProjectName { get => _projectName; set => SetField(ref _projectName, value); }

    public bool HasProject => _store is not null;

    public ICommand NewProjectCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand RenameProjectCommand { get; }
    public ICommand ProjectsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand AddSceneCommand { get; }
    public ICommand DeleteSceneCommand { get; }
    public ICommand MoveSceneUpCommand { get; }
    public ICommand MoveSceneDownCommand { get; }
    public ICommand DeleteTakeCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleRecordCommand { get; }
    private bool _hasBlockingDeviceIssue;
    /// <summary>
    /// True when the current scene has an enabled source whose saved
    /// device wasn't seen in the latest detection sweep. Drives the
    /// Record button's CanExecute.
    /// </summary>
    public bool HasBlockingDeviceIssue
    {
        get => _hasBlockingDeviceIssue;
        private set
        {
            if (SetField(ref _hasBlockingDeviceIssue, value))
                RaiseCommandStates();
        }
    }
    public ICommand AddPaletteSourceCommand { get; }
    public ICommand AddSceneSourceCommand { get; }

    /// <summary>Swappable for tests. Defaults to the real ffmpeg/NAudio recorder.</summary>
    public Func<ProjectStore, IRecorder> RecorderFactory { get; set; } =
        store => new Recorder(store);

    private IRecordingSession? _session;
    public bool IsRecording => _session is not null;
    /// <summary>Countdown in progress; the tray icon offers Cancel.</summary>
    public bool IsStarting => _starting;
    public string RecordButtonText => IsRecording ? "■ Stop" : "● Record";

    private string _recordingElapsed = "";
    public string RecordingElapsed
    {
        get => _recordingElapsed;
        set => SetField(ref _recordingElapsed, value);
    }

    public MainViewModel() : this(new NoopClipboardWriter()) { }

    /// <summary>
    /// Primary constructor. The clipboard writer is injected so unit tests
    /// can pass a stub; at runtime <c>App.OnFrameworkInitializationCompleted</c>
    /// installs one backed by the main window's Avalonia clipboard.
    /// </summary>
    public MainViewModel(IClipboardWriter clipboard)
    {
        _clipboard = clipboard;
        NewProjectCommand = new RelayCommand(() => NewProject(), () => !IsRecording);
        OpenProjectCommand = new RelayCommand(OpenProjectAsync, () => !IsRecording);
        RenameProjectCommand = new RelayCommand(RenameProjectAsync,
            () => _store is not null && !IsRecording);
        ProjectsCommand = new RelayCommand(ShowProjectBrowser, () => !IsRecording);
        SettingsCommand = new RelayCommand(ShowSettings);
        AddSceneCommand = new RelayCommand(() => Mutate(AddScene), () => HasProject);
        DeleteSceneCommand = new RelayCommand(() => Mutate(DeleteScene), () => HasProject && SelectedScene is not null && !IsRecording);
        MoveSceneUpCommand = new RelayCommand(() => Mutate(() => MoveScene(-1)), () => HasProject && SelectedScene is not null);
        MoveSceneDownCommand = new RelayCommand(() => Mutate(() => MoveScene(1)), () => HasProject && SelectedScene is not null);
        DeleteTakeCommand = new RelayCommand(() => Mutate(DeleteTake), () => HasProject && SelectedTake is not null && !IsRecording);
        ToggleFavoriteCommand = new RelayCommand(() => Mutate(ToggleFavorite), () => HasProject && SelectedTake is not null);
        ToggleRecordCommand = new RelayCommand(ToggleRecordAsync,
            () => HasProject && !_starting && !HasBlockingDeviceIssue &&
                  (IsRecording || SelectedScene is not null));
        _playerTimer.Tick += (_, _) => UpdatePlayerState();
        PlayerPlayPauseCommand = new RelayCommand(TogglePlayerPlayPauseAsync,
            () => _player is not null && !IsRecording);
        PlayerStopCommand = new RelayCommand(async () => await UnloadPlayerAsync(),
            () => _player is not null && !IsRecording);
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var chip = new TakeFilterChip(kind);
            chip.ToggleCommand = new RelayCommand(() =>
            {
                chip.IsActive = !chip.IsActive;
                RefreshTakes();
            });
            TakeFilterChips.Add(chip);
        }
        AddPaletteSourceCommand = new RelayCommand(AddPaletteSourceAsync, () => HasProject && !IsRecording);
        AddSceneSourceCommand = new RelayCommand(AddSceneSource,
            () => HasProject && !IsRecording && SelectedScene is not null && SelectedPaletteSourceToAdd is not null);
        ClearLogCommand = new RelayCommand(() => LogLines.Clear());
        // Builds the snapshot on the UI thread (LogLines is mutated there),
        // then hands the text to the injected clipboard. Errors land in the
        // standard RelayCommand crash reporter so a clipboard hiccup doesn't
        // get silently swallowed.
        CopyLogCommand = new RelayCommand(async () =>
        {
            var text = BuildLogText();
            try
            {
                await _clipboard.SetTextAsync(text);
                Status = "Log copied to clipboard";
            }
            catch (Exception ex)
            {
                var msg = $"Copy failed: {ex.Message}";
                Status = msg;
                Log(msg);
            }
        }, () => LogLines.Count > 0);
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            // Ensure on-disk logs exist even if Log() has never been
            // called yet — otherwise the user clicks "Open" and the OS
            // shows an empty folder, which is confusing.
            AppLog.Open();
            var path = AppLog.LogDirectory;
            if (OpenInOs.Open(path))
                Status = $"Opened log folder: {path}";
            else
            {
                Status = $"Could not open log folder: {path}";
                Log($"Could not open log folder: {path} (try opening it manually — see Status).");
            }
        });
    }

    /// <summary>
    /// Fallback clipboard writer used when no real one is registered. Does
    /// nothing; <see cref="CopyLogCommand"/> will silently succeed (so the UI
    /// stays responsive) and just skip the system clipboard. App's startup
    /// should install an Avalonia-backed writer — this is the safety net so
    /// a misconfigured run doesn't crash.
    /// </summary>
    private sealed class NoopClipboardWriter : IClipboardWriter
    {
        public Task SetTextAsync(string text) => Task.CompletedTask;
    }

    /// <summary>
    /// Called by MainWindow once it is shown. Opens the last project if there
    /// is one; otherwise creates a starter project. The project browser is
    /// only shown when the user clicks Projects.
    /// </summary>
    public async void StartupAsync()
    {
        var last = AppSettings.LastProjectDir;
        if (last is not null && Directory.Exists(last))
            OpenProject(last);
        if (_store is null)
            NewProject(); // first run: create a starter project so the UI isn't empty
        // Wait for device detection to finish before returning. The
        // dropdowns all show "Default" until RefreshDevicesAsync
        // completes — if the user opens one before then, they think
        // "Default" is the only option, and the picker never sees a
        // device pick to mirror into the global palette. Running the
        // detections in parallel and awaiting them all before the
        // window is shown is the simplest fix.
        if (PaletteSources.Count > 0)
        {
            var dets = PaletteSources.Select(ps => ps.RefreshDevicesAsync()).ToArray();
            await Task.WhenAll(dets);
        }
    }

    public async Task ShowProjectBrowser()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null) return;
        if (IsRecording) return;
        var win = new ProjectBrowserWindow(
            OpenProjectDir,
            name => NewProject(name),
            dir => RenameProjectDirAsync(dir),
            dir => DeleteProjectAsync(dir));
        await win.ShowDialog(desktop.MainWindow);
    }

    /// <summary>
    /// Deletes a project directory from disk. If the project is the one
    /// currently open, the in-memory store is cleared first and a
    /// fresh starter project is created so the UI doesn't end up in
    /// a stale state. Refreshes the global palette's
    /// <c>SourceOverrides</c> to drop any overrides pointing at the
    /// deleted project (orphan cleanup).
    /// </summary>
    public async Task DeleteProjectAsync(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        // If the project being deleted is the one we have open, unload
        // it and create a fresh placeholder so the UI stays usable.
        var isOpen = _store?.ProjectDir == dir;
        if (isOpen) { _ = UnloadPlayerAsync(); _store = null; }
        // Unload can race with the directory delete on Windows
        // (file handles); a brief Task.Yield lets the unload
        // settle. Keeps the delete itself synchronous so failures
        // surface immediately to the caller.
        await Task.Yield();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Reopen the original project if we were deleting the
            // open one and the delete failed.
            if (isOpen && Directory.Exists(dir))
                OpenProject(dir);
            throw;
        }
        // Drop SourceOverrides that point at this project's
        // source-overrides-by-id mapping… actually, the global
        // palette is keyed by source id, not project path, so
        // nothing to clean there. Project's per-source overrides
        // went with the folder.
        // If we just removed the open project, replace it with a
        // fresh starter so the user has somewhere to land.
        if (isOpen)
        {
            NewProject();
        }
        Status = $"Deleted project: {dir}";
    }

    public void OpenProjectDir(string dir)
    {
        if (_store is not null && string.Equals(_store.ProjectDir, dir, StringComparison.OrdinalIgnoreCase))
            return;
        OpenProject(dir);
    }

    private void ShowSettings()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null) return;
        var win = new SettingsWindow();
        _ = win.ShowDialog(desktop.MainWindow);
    }

    private void Mutate(Action action)
    {
        action();
        RaiseCommandStates();
        Raise(nameof(HasProject));
    }

    /// <summary>
    /// Re-evaluates every command's CanExecute. Needed whenever HasProject,
    /// SelectedScene, or recording state changes outside of Mutate —
    /// notably after OpenProject, which is why the Record button used to
    /// stay dead until a source was toggled.
    /// </summary>
    private void RaiseCommandStates()
    {
        foreach (var c in new[] { AddSceneCommand, DeleteSceneCommand, MoveSceneUpCommand,
                                  MoveSceneDownCommand, DeleteTakeCommand, ToggleFavoriteCommand,
                                  ToggleRecordCommand, NewProjectCommand, OpenProjectCommand,
                                  RenameProjectCommand, ProjectsCommand, AddPaletteSourceCommand,
                                  AddSceneSourceCommand, PlayerPlayPauseCommand }
                 .OfType<RelayCommand>())
            c.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Shows a confirmation dialog. Returns true when the user picks the
    /// primary action, false on Cancel / window close. The primary button
    /// is labelled with <paramref name="confirmText"/> — default is
    /// "Delete" for backwards compatibility with destructive dialogs that
    /// pass only a title and message. Non-destructive callers should
    /// pass a verb that matches their action ("Normalize", "Reset", etc.)
    /// so the user isn't asked to "Delete" a take they're not deleting.
    /// </summary>
    private async Task<bool> ConfirmAsync(string title, string message,
        string confirmText = "Delete")
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null) return false;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Avalonia.Controls.Window
        {
            Title = title,
            Width = 420, Height = 170,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
        };
        var panel = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 16,
        };
        panel.Children.Add(new Avalonia.Controls.TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var buttons = new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        // Cancel on the left, primary on the right — matches every
        // desktop OS convention and keeps destructive actions visibly
        // "last" in the tab order. Enter still triggers the focused
        // button (default IsCancel/IsDefault on Cancel here).
        var cancel = new Avalonia.Controls.Button { Content = "Cancel" };
        var primary = new Avalonia.Controls.Button { Content = confirmText };
        primary.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(primary);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        try { await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(false); }
        catch { tcs.TrySetResult(false); }
        return await tcs.Task.ConfigureAwait(false);
    }

    // ----- projects -----

    private static string SanitizeProjectName(string name)
    {
        var clean = string.Concat(name.Trim().Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) ? "Untitled Tutorial" : clean;
    }

    public void NewProject(string? name = null)
    {
        var title = string.IsNullOrWhiteSpace(name) ? "Untitled Tutorial" : name.Trim();
        var folder = SanitizeProjectName(title);
        var dir = Path.Combine(AppSettings.ProjectsDir, folder + ".csp");
        if (Directory.Exists(dir))
            dir = Path.Combine(AppSettings.ProjectsDir,
                $"{folder}-{DateTime.Now:yyyyMMdd-HHmmss}.csp");
        var store = ProjectStore.Create(dir, title);
        store.AddScene("Intro");
        store.AddScene("Demo");
        OpenProject(dir);
        Status = $"Created {dir}";
    }

    /// <summary>
    /// Prompts for a new name, sanitizes it, and writes it to the
    /// current project's project.json. Does NOT rename the on-disk
    /// folder (the folder is the project's identity for any external
    /// tooling — file watchers, paths in scenes, take filenames —
    /// and a surprise move could break those). The folder can be
    /// renamed by hand if needed.
    /// </summary>
    public async void RenameProjectAsync()
    {
        if (_store is null || IsRecording) return;
        await RenameProjectAtAsync(_store.ProjectDir, restoreCurrent: false);
    }

    /// <summary>
    /// Same as <see cref="RenameProjectAsync"/> but takes an explicit
    /// project directory so the project browser can rename an
    /// arbitrary project (not just the currently-open one). If the
    /// requested project is the currently-open one we stay on it;
    /// otherwise we open it, rename, and reopen whatever was open
    /// before so the user's editing context isn't disturbed.
    /// </summary>
    public async Task RenameProjectDirAsync(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        if (IsRecording) return;
        var openDir = _store?.ProjectDir;
        await RenameProjectAtAsync(dir, restoreCurrent: openDir is not null && openDir != dir);
    }

    private async Task RenameProjectAtAsync(string dir, bool restoreCurrent)
    {
        string? newName = null;
        string? oldOpenDir = null;
        if (restoreCurrent) { oldOpenDir = _store?.ProjectDir; UnloadPlayerQuiet(); _store = null; }
        try
        {
            // Open just for the rename — we close it again below.
            var tempStore = ProjectStore.Open(dir);
            var current = tempStore.Project.Name;
            var input = await PromptTextAsync("Rename project",
                "New project name:", current);
            if (input is null) { Status = "Rename cancelled."; return; }
            newName = input.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                Status = "Project name can't be empty.";
                return;
            }
            if (newName == current)
            {
                Status = "Project name unchanged.";
                return;
            }
            tempStore.Project.Name = newName;
            tempStore.SaveProject();
            Status = $"Renamed project to '{newName}' (folder unchanged).";
        }
        catch (Exception ex)
        {
            Status = $"Rename failed: {ex.Message}";
            return;
        }
        finally
        {
            if (restoreCurrent && oldOpenDir is not null && Directory.Exists(oldOpenDir))
            {
                try { OpenProject(oldOpenDir); }
                catch { /* leave it unloaded if the open fails */ }
            }
        }
    }

    /// <summary>
    /// Hushes the player before a store swap so the user doesn't see
    /// "stopped" status flicker in the UI. No-op if no player is loaded.
    /// </summary>
    private void UnloadPlayerQuiet() => _ = UnloadPlayerAsync();

    /// <summary>
    /// Modal single-line input dialog. Returns the entered text, or
    /// null if the user cancelled. Enter = accept, Esc = cancel.
    /// </summary>
    private async Task<string?> PromptTextAsync(string title, string message,
        string initialValue)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null) return null;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Avalonia.Controls.Window
        {
            Title = title,
            Width = 460, Height = 180,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
        };
        var panel = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 12,
        };
        panel.Children.Add(new Avalonia.Controls.TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var box = new Avalonia.Controls.TextBox
        {
            Text = initialValue,
            // Select-all so the user can just type a new name without
            // having to clear the field first.
        };
        panel.Children.Add(box);
        var buttons = new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        var cancel = new Avalonia.Controls.Button { Content = "Cancel" };
        var ok = new Avalonia.Controls.Button { Content = "Rename",
                                              IsDefault = true };
        ok.Click += (_, _) => { tcs.TrySetResult(box.Text); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        // Enter on the textbox → accept. Esc → cancel.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                tcs.TrySetResult(box.Text);
                dialog.Close();
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                tcs.TrySetResult(null);
                dialog.Close();
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Closed += (_, _) => tcs.TrySetResult(null);
        // Focus the textbox and select-all once it's loaded so the user
        // can immediately type to replace the existing name.
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        try { await dialog.ShowDialog(desktop.MainWindow).ConfigureAwait(false); }
        catch { tcs.TrySetResult(null); }
        return await tcs.Task.ConfigureAwait(false);
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
        Raise(nameof(ProjectFolderText));
        RaiseCommandStates();
    }

    public string ProjectFolderText =>
        _store is null ? "" : $"Folder: {Path.GetFileName(_store.ProjectDir)}";

    // ----- scenes -----

    private void ReloadScenes()
    {
        Scenes.Clear();
        if (_store is null) return;
        foreach (var s in _store.LoadScenes())
        {
            var vm = new SceneViewModel(_store, s);
            vm.TakeCount = _store.LoadTakes(s.FolderName).Count;
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
        // The Sources panel shows the project's own palette (Project.Sources).
        // Each entry carries the project's per-source picks — device, badge,
        // label — which is why they stick around project-to-project: the
        // project owns them, the global registry is just a kinds-pool for
        // adding new entries.
        foreach (var p in _store.Project.Sources)
        {
            // Pass a copy of the entry so the VM can mutate its
            // own Badge/Device mid-edit without surprising the user with
            // mid-edit changes to the underlying source from elsewhere.
            var projectView = new ProjectSource
            {
                Id = p.Id, Kind = p.Kind, Label = p.Label, Badge = p.Badge,
                Device = p.Device,
            };
            PaletteSourceViewModel? vm = null;
            vm = new PaletteSourceViewModel(projectView,
                () => SavePaletteSource(p.Id, vm!),
                RefreshSceneDeviceLabels);
            vm.DeleteCommand = new RelayCommand(
                () => Mutate(() => DeletePaletteSource(vm!)),
                () => !IsRecording);
            vm.OnBadgeChanged = () => { RefreshSceneSources(); RefreshTakes(); };
            // Re-evaluate the Record gate when any source's missing-device
            // status flips. Subscribe to PropertyChanged and watch for
            // IsDeviceMissing — cheap because Raise only fires on change.
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PaletteSourceViewModel.IsDeviceMissing))
                    RecomputeBlockingDeviceIssue();
            };
            PaletteSources.Add(vm);
            _ = vm.RefreshDevicesAsync(); // fire-and-forget background detection
        }
        // Palette VMs were rebuilt; re-wire the scene rows' device labels.
        foreach (var svm in SceneSources)
            svm.SetPaletteSource(
                PaletteSources.FirstOrDefault(ps => ps.Source.Id == svm.SceneSource.SourceId));
        Raise(nameof(AvailablePaletteSources));
        // Initial state: detection hasn't run yet, so no source is
        // "missing" (we treat indeterminate as not-blocking). Refresh
        // after detection completes — see vm.PropertyChanged above.
        RecomputeBlockingDeviceIssue();
    }

    /// <summary>
    /// Persists the user's edits to a project palette entry (device,
    /// badge, label) into the current project's <c>Project.Sources</c>.
    /// The global registry is a separate kinds-pool; per-project picks
    /// don't touch it. Saving through this method is what makes the
    /// settings stick to THIS project — switching projects and back
    /// reads the same data from <c>Project.Sources</c>.
    /// </summary>
    private void SavePaletteSource(string paletteId, PaletteSourceViewModel vm)
    {
        if (_store is null) return;
        var entry = _store.Project.Sources.FirstOrDefault(s => s.Id == paletteId);
        if (entry is null) return;
        DebugPalette($"save: paletteId={paletteId} device={vm.Source.Device ?? "<null>"} badge={vm.Source.Badge ?? "<null>"}");
        var changed = false;
        if (entry.Device != vm.Source.Device) { entry.Device = vm.Source.Device; changed = true; }
        if (entry.Badge != vm.Source.Badge) { entry.Badge = vm.Source.Badge; changed = true; }
        if (entry.Label != vm.Source.Label) { entry.Label = vm.Source.Label; changed = true; }
        if (!changed) return;
        try { _store.SaveProject(); }
        catch (Exception ex) { DebugPalette($"save: FAILED: {ex.Message}"); throw; }
    }

    // Diagnostic logging for palette save debugging. Off by default;
    // set CAPTURESPIKE_PALETTE_TRACE=1 in the environment to enable.
    private static bool PaletteTraceEnabled =>
        System.Environment.GetEnvironmentVariable("CAPTURESPIKE_PALETTE_TRACE") == "1";
    private void DebugPalette(string msg)
    {
        if (!PaletteTraceEnabled) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.DataContext is not MainViewModel vm) return;
        vm.Log($"[palette] {msg}");
    }

    /// <summary>
    /// Walks the current scene's enabled sources and checks whether any
    /// has a missing device. Sets <see cref="HasBlockingDeviceIssue"/>.
    /// </summary>
    private void RecomputeBlockingDeviceIssue()
    {
        var scene = SelectedScene?.Scene;
        if (scene is null) { HasBlockingDeviceIssue = false; return; }
        var byId = PaletteSources.ToDictionary(ps => ps.Source.Id, ps => ps);
        var any = scene.Sources
            .Where(ss => ss.Enabled)
            .Any(ss => byId.TryGetValue(ss.SourceId, out var p) && p.IsDeviceMissing);
        HasBlockingDeviceIssue = any;
    }

    private void RefreshSceneDeviceLabels()
    {
        foreach (var vm in SceneSources) vm.RaiseDeviceLabel();
    }

    private void AddScene()
    {
        if (_store is null) return;
        var scene = _store.AddScene($"Scene {Scenes.Count + 1}");
        var vm = new SceneViewModel(_store, scene);
        Scenes.Add(vm);
        SelectedScene = vm;
        Status = $"Added scene \"{scene.Title}\"";
    }

    private async void DeleteScene()
    {
        if (_store is null || SelectedScene is null || IsRecording) return;
        var title = SelectedScene.Title;
        if (!await ConfirmAsync("Delete scene",
                $"Delete scene \"{title}\" and all its takes? This cannot be undone."))
            return;
        try
        {
            _store.DeleteScene(SelectedScene.Scene);
            ReloadScenes();
            Status = $"Deleted scene \"{title}\"";
        }
        catch (Exception ex)
        {
            Status = $"Delete failed: {ex.Message}";
        }
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

    private int _nextTakeNumber = 1;

    private void RefreshTakes(Take? selectTake = null, string? selectId = null)
    {
        var selectedId = selectTake?.Id ?? selectId ?? SelectedTake?.Take.Id;
        // Don't let the refresh itself unload the player; only a real
        // selection change does that.
        _selectedTake = null;
        Takes.Clear();
        Raise(nameof(SelectedTake));
        Raise(nameof(TakeHeaderText));
        if (_store is null || SelectedScene is null) return;
        var fav = SelectedScene.Scene.FavoriteTakeId;
        var sceneFolder = SelectedScene.Scene.FolderName;
        var all = _store.LoadTakes(sceneFolder);
        _nextTakeNumber = all.Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        var activeKinds = TakeFilterChips.Where(c => c.IsActive).Select(c => c.Kind).ToHashSet();
        var n = 1;
        foreach (var t in all)
        {
            if (activeKinds.Count > 0 && !activeKinds.All(k => t.Sources.Any(s => s.Kind == k)))
                continue;
            Takes.Add(new TakeRow(t, n++, t.Id == fav, _store.TakeDir(sceneFolder, t.Id),
                ToggleFavoriteRow, DeleteTakeRow, OpenTakeFolder,
                NormalizeTakeRow,
                id => _store.Project.Sources.FirstOrDefault(p => p.Id == id)?.Label
                       ?? _palette.Sources.FirstOrDefault(p => p.Id == id)?.Label,
                id => _store.Project.Sources.FirstOrDefault(p => p.Id == id)?.EffectiveBadge()
                       ?? _palette.Sources.FirstOrDefault(p => p.Id == id)?.EffectiveBadge(),
                PlayTakeMultiWindow,
                row =>
                {
                    _store.SaveTake(_store.TakeDir(sceneFolder, row.Take.Id), row.Take);
                    // If this take is currently playing, push offset updates live
                    // and re-seek so the change is visible immediately.
                    // Capture the take-time BEFORE updating offsets.
                    if (_player is TiledTakePlayer tiled && _playerTake?.Id == row.Take.Id)
                    {
                        var takeTime = tiled.Position;
                        foreach (var s in row.Take.Sources)
                            tiled.UpdateSourceOffset(s.Id, s.EffectiveOffsetMs);
                        // Observe failures; overlapping seeks are harmless
                        // (idempotent — last one wins).
                        _ = tiled.SeekAsync(takeTime).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                Status = $"Offset live-seek: {t.Exception?.GetBaseException().Message}";
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }));
        }
        SelectedScene.TakeCount = all.Count;
        SelectedTake = Takes.FirstOrDefault(t => t.Take.Id == selectedId);
        Raise(nameof(NextTakeText));
    }

    private void RefreshSceneSources()
    {
        SceneSources.Clear();
        _selectedPaletteSourceToAdd = null;
        if (_store is null || SelectedScene is null) return;
        // Sources live in the global palette, not on the project.
        // _store.Project.Sources is empty after migration; we need
        // the global lookup to find each scene source's palette entry.
        var global = _palette.Sources;
        foreach (var ss in SelectedScene.Scene.Sources)
        {
            var palette = global.FirstOrDefault(p => p.Id == ss.SourceId);
            if (palette is null) continue; // dangling ref; cleaned on next save
            var vm = new SceneSourceViewModel(ss, palette);
            vm.SetPaletteSource(
                PaletteSources.FirstOrDefault(p => p.Source.Id == ss.SourceId));
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
        // Kind lookup uses the project's palette, falling back to the
        // global registry for kinds the project hasn't been seeded with
        // yet (e.g., a kind the user just added in another project).
        var registry = _palette.Sources;
        var local = _store.Project.Sources;
        bool IsVideo(string id) =>
            (local.FirstOrDefault(p => p.Id == id)
                ?? registry.FirstOrDefault(p => p.Id == id))?.Kind.IsVideo() == true;
        var videoCount = SelectedScene.Scene.Sources.Count(s => IsVideo(s.SourceId));
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
            return $"Next: Take {_nextTakeNumber:D2} · {on} source{(on == 1 ? "" : "s")} on";
        }
    }

    private async void DeleteTakeRow(TakeRow row)
    {
        if (_store is null || SelectedScene is null || IsRecording) return;
        var label = row.DisplayName;
        if (!await ConfirmAsync("Delete take",
                $"Delete {label}? The take folder and all its files will be removed. This cannot be undone."))
            return;
        try
        {
            if (_playerTake?.Id == row.Take.Id)
                await UnloadPlayerAsync();
            _store.DeleteTake(SelectedScene.Scene.FolderName, row.Take);
            RefreshTakes();
            Status = $"Deleted {label}";
        }
        catch (Exception ex)
        {
            Status = $"Delete failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Normalize the take: write aligned copies of every source file
    /// (named "&lt;name&gt;.normalized.&lt;ext&gt;") and zero the per-source
    /// offsets. Originals are kept alongside. The take's player, if
    /// it's currently playing this take, is unloaded first because
    /// the underlying file references will change.
    /// </summary>
    private async void NormalizeTakeRow(TakeRow row)
    {
        if (_store is null || SelectedScene is null || IsRecording) return;
        var label = row.DisplayName;
        var scene = SelectedScene.Scene;
        var takeDir = _store.TakeDir(scene.FolderName, row.Take.Id);
        var n = row.Take.Sources.Count;
        if (!await ConfirmAsync("Normalize take",
                $"Normalize {label}? This writes aligned copies of {n} source file{(n == 1 ? "" : "s")} " +
                $"and resets their offsets. Originals are kept alongside.",
                confirmText: "Normalize"))
            return;
        try
        {
            if (_playerTake?.Id == row.Take.Id)
                await UnloadPlayerAsync();
            Status = $"Normalizing {label}…";
            var result = await CaptureSpike.Core.Playback.TakeNormalizer.NormalizeAsync(
                takeDir, row.Take,
                (sourceId, step, message) =>
                {
                    if (step is double p)
                        Status = $"Normalize {label} [{sourceId}]: {p * 100:F0}% — {message}";
                    else
                        Status = $"Normalize {label}: {message}";
                });
            if (result.Failed.Count > 0)
            {
                Status = $"Normalize {label}: {result.Failed.Count} source(s) failed — {string.Join("; ", result.Failed)}";
                return;
            }
            // Apply the new sources back to the take, save, and reload the take list.
            row.Take.Sources.Clear();
            foreach (var s in result.UpdatedSources) row.Take.Sources.Add(s);
            _store.SaveTake(takeDir, row.Take);
            RefreshTakes();
            Status = $"Normalized {label}: {result.UpdatedSources.Count} source(s) aligned.";
        }
        catch (Exception ex)
        {
            Status = $"Normalize failed: {ex.Message}";
        }
    }

    private const string FavoriteSuffix = "-favorite";

    private static string FavoriteId(string takeId, bool favorite)
    {
        var baseId = takeId.EndsWith(FavoriteSuffix, StringComparison.Ordinal)
            ? takeId[..^FavoriteSuffix.Length] : takeId;
        return favorite ? baseId + FavoriteSuffix : baseId;
    }

    private async void ToggleFavoriteRow(TakeRow row)
    {
        if (_store is null || SelectedScene is null) return;
        var scene = SelectedScene.Scene;
        var id = row.Take.Id;
        var wasFav = scene.FavoriteTakeId == id;

        // The take's folder is about to move; don't leave the player on it.
        if (_playerTake?.Id == id || (_playerTake is not null && scene.FavoriteTakeId == _playerTake.Id))
            await UnloadPlayerAsync();

        string? selectId = null;
        if (!wasFav && scene.FavoriteTakeId is not null)
        {
            // Switching favorites: strip the suffix off the old one.
            var oldFav = scene.FavoriteTakeId;
            var unmarked = _store.RenameTake(scene.FolderName, oldFav, FavoriteId(oldFav, false));
            if (SelectedTake?.Take.Id == oldFav) selectId = unmarked;
        }

        string newId;
        if (wasFav)
        {
            newId = _store.RenameTake(scene.FolderName, id, FavoriteId(id, false));
            scene.FavoriteTakeId = null;
        }
        else
        {
            newId = _store.RenameTake(scene.FolderName, id, FavoriteId(id, true));
            scene.FavoriteTakeId = newId;
        }
        if (SelectedTake?.Take.Id == id) selectId = newId;

        _store.SaveScene(scene);
        RefreshTakes(selectId: selectId);
        Status = wasFav ? $"Unmarked {row.DisplayName} as favorite" : $"Marked {row.DisplayName} as favorite";
    }

    private void OpenTakeFolder(TakeRow row)
    {
        if (_store is null || SelectedScene is null) return;
        var path = _store.TakeDir(SelectedScene.Scene.FolderName, row.Take.Id);
        try
        {
            if (!Directory.Exists(path))
            {
                Status = "Take folder no longer exists.";
                return;
            }
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                : new ProcessStartInfo("xdg-open", $"\"{path}\"");
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open the take folder: {ex.Message}";
        }
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
        try
        {
            // Two-step add: the new kind lands in the global registry
            // (so other projects can use it) AND in this project's
            // palette (so the user sees it immediately). The registry
            // is the kinds-pool — no device selection there. The
            // project entry gets the user's id/kind/label; device
            // stays null until the user picks one.
            if (!_palette.Contains(result.Id))
            {
                _palette.Add(new ProjectSource
                {
                    Id = result.Id, Kind = result.Kind, Label = result.Label,
                    Badge = result.Badge,
                    // Device intentionally not copied to the registry.
                });
                _palette.Save();
            }
            if (!_store.Project.Sources.Any(s => s.Id == result.Id))
            {
                _store.Project.Sources.Add(new ProjectSource
                {
                    Id = result.Id, Kind = result.Kind, Label = result.Label,
                    Badge = result.Badge, Device = result.Device,
                });
                _store.SaveProject();
            }
            Mutate(ReloadPalette);
            Status = $"Added source \"{result.Label}\" to the palette.";
        }
        catch (Exception ex)
        {
            Status = $"Add source failed: {ex.Message}";
        }
    }

    private async void DeletePaletteSource(PaletteSourceViewModel vm)
    {
        if (_store is null || IsRecording) return;
        if (!await ConfirmAsync("Delete source",
                $"Delete source \"{vm.Label}\" from the palette? It will be removed from all scenes in every project. This cannot be undone."))
            return;
        try
        {
            var id = vm.Source.Id;
            // Drop from the global kinds registry and from this
            // project's palette. Other projects have their own
            // copies on disk; they get cleaned up on next open.
            _palette.Remove(id);
            _palette.Save();
            _store.Project.Sources.RemoveAll(s => s.Id == id);
            _store.SaveProject();
            // Strip from the in-memory scenes the UI is holding for the
            // current project; otherwise the dangling reference stays
            // enabled and breaks the next recording. Other projects
            // have their own scene state in memory; they'll be cleaned
            // up next time they open.
            foreach (var svm in Scenes)
                svm.Scene.Sources.RemoveAll(s => s.SourceId == id);
            ReloadPalette();
            RefreshSceneSources();
            Status = $"Deleted source \"{vm.Label}\"";
        }
        catch (Exception ex)
        {
            Status = $"Delete failed: {ex.Message}";
        }
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
            await StopRecordingAsync();
            return;
        }
        if (SelectedScene is null) return;
        if (HasBlockingDeviceIssue)
        {
            // Safety net in case the button is enabled by a stale
            // CanExecute before RecomputeBlockingDeviceIssue catches up.
            Status = "Can't record: a source device is missing. Pick a connected device or remove the source from this scene.";
            return;
        }
        await StartRecordingAsync();
    }

    private static Avalonia.Controls.Window? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private bool _starting; // countdown in progress; recorder not yet running
    private volatile bool _cancelStarting;
    /// <summary>Window state before recording minimized it; restored on stop.</summary>
    private Avalonia.Controls.WindowState _preRecordWindowState =
        Avalonia.Controls.WindowState.Normal;

    private async Task StartRecordingAsync()
    {
        if (_store is null || SelectedScene is null || _session is not null || _starting) return;
        _starting = true;
        // A stale flag from a previous cancel must not cancel this countdown.
        _cancelStarting = false;
        RaiseRecordingState();
        string? takeDir = null;
        try
        {
            // The window minimizes during the countdown so it never ends up in
            // its own screen recording. Remember the state to restore after.
            var window = MainWindow;
            if (window is not null)
            {
                _preRecordWindowState = window.WindowState == Avalonia.Controls.WindowState.Minimized
                    ? Avalonia.Controls.WindowState.Normal
                    : window.WindowState;
                window.WindowState = Avalonia.Controls.WindowState.Minimized;
            }
            RecordingElapsed = "starting…";

            // Don't let the preview's audio bleed into the new take's mic.
            if (_player is not null)
            {
                try { await _player.PauseAsync(); } catch { }
            }

            var scene = SelectedScene.Scene;
            takeDir = _store.BeginTake(scene);
            var progress = new Progress<TimeSpan>(t => RecordingElapsed = t.ToString(@"m\:ss"));

            // Scheduled start: t0 is now; sources warm up IN PARALLEL during
            // the 3s countdown (instead of starting one-by-one after it).
            // The tray Cancel menu sets _cancelStarting, which aborts the startup.
            using var cts = new CancellationTokenSource();
            var startTask = RecorderFactory(_store).StartAsync(
                _store.Project, scene, takeDir, progress, cts.Token);

            // Countdown overlaps warmup: poll for cancel every 100ms.
            // (No ConfigureAwait(false): the code below touches UI-bound state.)
            for (var i = 0; i < 30 && !_cancelStarting; i++)
                await Task.Delay(100);

            if (_cancelStarting)
            {
                _cancelStarting = false;
                cts.Cancel();
                IRecordingSession? leaked = null;
                Exception? startError = null;
                try { leaked = await startTask; }
                catch (Exception ex) { startError = ex; }
                // Race: sources finished starting just as we cancelled.
                // Dispose the session instead of leaking it (halt without
                // writing a take).
                if (leaked is not null)
                {
                    try { await leaked.DisposeAsync(); } catch { }
                }
                // Don't swallow a real device failure as "cancelled".
                if (startError is not null && startError is not OperationCanceledException)
                    Status = $"Record failed: {startError.Message}";
                else
                    Status = "Recording cancelled.";
            }
            else
            {
                try
                {
                    _session = await startTask;
                    // Cancel pressed while the last sources were starting: the user
                    // meant stop, so stop immediately instead of poisoning the
                    // next countdown with a stale flag.
                    if (_cancelStarting)
                    {
                        _cancelStarting = false;
                        await StopRecordingAsync();
                    }
                    else
                    {
                        Status = $"Recording '{scene.Title}' — stop from the tray icon";
                        Log($"Recording started: '{scene.Title}' ({_session.ActiveSourceIds.Count} sources)");
                    }
                }
                catch (Exception ex)
                {
                    _session = null;
                    // Don't leave an orphan take folder (possibly with partial files).
                    try { if (Directory.Exists(takeDir)) Directory.Delete(takeDir, recursive: true); } catch { }
                    takeDir = null;
                    Status = $"Record failed: {ex.Message}";
                }
            }

            if (_session is null)
            {
                // Cancelled: also remove the reserved take folder.
                try { if (takeDir is not null && Directory.Exists(takeDir)) Directory.Delete(takeDir, recursive: true); } catch { }
                RestoreWindowAfterRecording();
                RecordingElapsed = "";
            }
        }
        finally
        {
            _starting = false;
            RaiseRecordingState();
        }
    }

    /// <summary>
    /// Stops the recording, or cancels the countdown. Wired to the tray
    /// icon's Stop menu (the reliable replacement for the old F9 hotkey).
    /// </summary>
    public async void StopRecordingFromTray()
    {
        if (_starting)
        {
            _cancelStarting = true;
            return;
        }
        if (_session is not null)
            await StopRecordingAsync();
    }

    /// <summary>
    /// Shows the main window, restoring the pre-recording state when it was
    /// minimized for a recording. Wired to the tray icon.
    /// </summary>
    public void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null) return;
        window.Show();
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
            window.WindowState = (IsRecording || _starting)
                ? _preRecordWindowState
                : Avalonia.Controls.WindowState.Normal;
        window.Activate();
    }

    /// <summary>Stops recording and saves the take. Safe to call when not recording.</summary>
    public async Task StopRecordingAsync()
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        RaiseRecordingState();
        try
        {
            var take = await session.StopAsync();
            await session.DisposeAsync();
            RefreshTakes(take);
            Status = $"Take {take.Number:D2} saved — {take.Sources.Count} sources, {take.DurationSeconds:F1}s";
        }
        catch (Exception ex)
        {
            Status = $"Stop failed: {ex.Message}";
        }
        finally
        {
            RestoreWindowAfterRecording();
        }
    }

    /// <summary>Brings the window back to its pre-recording state.</summary>
    private void RestoreWindowAfterRecording()
    {
        var window = MainWindow;
        if (window is null) return;
        window.WindowState = _preRecordWindowState;
        window.Activate();
    }

    private void RaiseRecordingState()
    {
        Raise(nameof(IsRecording));
        Raise(nameof(IsStarting));
        Raise(nameof(RecordButtonText));
        if (!IsRecording) RecordingElapsed = "";
        foreach (var c in new[] { ToggleRecordCommand, NewProjectCommand, OpenProjectCommand,
                                  RenameProjectCommand, ProjectsCommand, PlayerPlayPauseCommand,
                                  DeleteSceneCommand, DeleteTakeCommand }
                 .OfType<RelayCommand>())
            c.RaiseCanExecuteChanged();
        foreach (var ps in PaletteSources)
            (ps.DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ----- player -----

    private IPlayer? _player;
    private TiledPlaybackWindow? _tiledWindow;
    private Take? _playerTake;
    private string? _playerSceneId;
    private string? _playerError;
    private string? _playerDisplayName;

    /// <summary>What the preview is currently playing, e.g. "Take 05 · screen.mp4".</summary>
    private string _nowPlaying = "";
    public string NowPlayingText { get => _nowPlaying; set => SetField(ref _nowPlaying, value); }

    /// <summary>Caption under the preview: sync note for full takes, single-file note otherwise.</summary>
    private string _playerCaption = "";
    public string PlayerCaptionText { get => _playerCaption; set => SetField(ref _playerCaption, value); }
    private bool _playerScrubbing;
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public ObservableCollection<PlayerTrackRow> PlayerTracks { get; } = new();

    public bool HasPlayerTake => _playerTake is not null;
    /// <summary>True when the take is playing in per-source windows.</summary>
    public bool IsMultiWindow => _player is TiledTakePlayer;
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

    // ----- ITransportHost -----
    // Implemented so the popup's PlayerTransportBar can drive playback
    // and read state without reaching into private fields.

    TimeSpan ITransportHost.Position => TimeSpan.FromSeconds(_playerPositionSeconds);
    TimeSpan ITransportHost.Duration => TimeSpan.FromSeconds(_playerDurationSeconds);
    bool ITransportHost.IsPlaying => _isPlayerPlaying;
    string ITransportHost.PlayPauseGlyph => IsPlayerPlaying ? "⏸ Pause" : "▶ Play";

    void ITransportHost.PlayPause() => TogglePlayerPlayPauseAsync();
    void ITransportHost.Stop() => _ = UnloadPlayerAsync();
    void ITransportHost.SeekTo(double seconds)
    {
        // Same pause-seek-resume dance EndScrub does, but exposed via
        // the transport interface so the popup's slider can drive it.
        // EndScrub is async void (it owns its own try/catch for status
        // reporting), so we just call it and let it run.
        _playerScrubbing = false;
        EndScrub(seconds);
    }

    public ICommand PlayerPlayPauseCommand { get; }
    public ICommand PlayerStopCommand { get; }

    /// <summary>
    /// Plays a take with all video sources tiled in one window, in sync.
    /// </summary>
    private async void PlayTakeMultiWindow(TakeRow row)
    {
        if (_store is null || IsRecording) return;

        await UnloadPlayerAsync();
        var scene = SelectedScene?.Scene;
        if (scene is null) return;

        var take = row.Take;
        var takeDir = _store.TakeDir(scene.FolderName, take.Id);
        _playerTake = take;
        _playerSceneId = scene.Id;
        _playerDisplayName = $"{row.DisplayName} · tiled";
        _playerError = null;
        NowPlayingText = $"▶ {_playerDisplayName} ({take.Sources.Count} sources)";
        PlayerCaptionText = "All sources play in sync in one tiled window. Toggle sources below.";
        Raise(nameof(HasPlayerTake));
        Raise(nameof(PlayerPlaceholderText));

        // No ConfigureAwait(false): the code below touches UI-bound state.
        try
        {
            // Collect sources with existing files.
            var items = new List<(TakeSource Source, string Label, string Badge, string FilePath, bool IsVideo)>();
            var missing = new List<string>();
            var suspicious = new List<string>();
            foreach (var ts in take.Sources)
            {
                var filePath = Path.Combine(takeDir, ts.File);
                var label = _store.Project.Sources.FirstOrDefault(p => p.Id == ts.Id)?.Label
                    ?? _palette.Sources.FirstOrDefault(p => p.Id == ts.Id)?.Label
                    ?? ts.Kind.DefaultLabel();
                if (!File.Exists(filePath))
                {
                    missing.Add($"{label} ({ts.File})");
                    continue;
                }
                // Flag suspiciously small files (likely a failed recording).
                var fileBytes = new FileInfo(filePath).Length;
                if (fileBytes < 1024)
                    suspicious.Add($"{label} ({ts.File} is only {fileBytes} bytes)");
                var badge = _store.Project.Sources.FirstOrDefault(p => p.Id == ts.Id)?.EffectiveBadge()
                    ?? _palette.Sources.FirstOrDefault(p => p.Id == ts.Id)?.EffectiveBadge()
                    ?? ts.Kind.Badge();
                var isVideo = !Path.GetExtension(filePath).Equals(".wav", StringComparison.OrdinalIgnoreCase);
                items.Add((ts, label, badge, filePath, isVideo));
            }
            if (missing.Count > 0)
                Status = $"Take {row.DisplayName}: missing files for {string.Join(", ", missing)}.";
            else if (suspicious.Count > 0)
                Status = $"Take {row.DisplayName}: suspiciously small files: {string.Join(", ", suspicious)}.";
            if (items.Count == 0)
            {
                _playerError = "No playable source files in this take.";
                Raise(nameof(PlayerPlaceholderText));
                return;
            }

            // One tiled window for all sources (video tiles + audio tiles).
            TiledPlaybackWindow? tiledWindow = null;
            Dictionary<string, nint> handles = new();
            if (items.Count > 0)
            {
                tiledWindow = new TiledPlaybackWindow(
                    $"{row.DisplayName} · {items.Count} source{(items.Count == 1 ? "" : "s")}",
                    items.Select(i => new TileSourceInfo(
                        i.Source.Id, i.Label, i.Badge,
                        Path.GetFileName(i.FilePath), i.IsVideo)).ToList(),
                    sourceId =>
                    {
                        // Audio tile clicked: toggle mute for that source.
                        var track = PlayerTracks.FirstOrDefault(t => t.SourceId == sourceId);
                        if (track is not null) ToggleTrackMuteAsync(track);
                    },
                    transport: this); // MainViewModel is the ITransportHost.
                tiledWindow.Closed += (_, _) => _ = UnloadPlayerAsync();
                tiledWindow.Show();
                handles = await tiledWindow.WaitForHandlesAsync(TimeSpan.FromSeconds(5));
            }

            var player = new TiledTakePlayer(take.DurationSeconds);
            var offsetInfo = new List<string>();
            foreach (var (source, label, _, filePath, isVideo) in items)
            {
                var handle = isVideo && handles.TryGetValue(source.Id, out var h) ? h : nint.Zero;
                await player.AddSourceAsync(source, filePath, handle);
                var eff = source.EffectiveOffsetMs;
                var manual = source.ManualOffsetMs is not null ? " (manual)" : "";
                offsetInfo.Add($"{label}={eff:F0}ms{manual}");
            }
            Status = $"Playing {row.DisplayName} with offsets: {string.Join(", ", offsetInfo)}";
            _player = player;
            _tiledWindow = tiledWindow;
            Raise(nameof(IsMultiWindow));

            PlayerTracks.Clear();
            foreach (var (source, label, badge, _, _) in items)
            {
                var trackRow = new PlayerTrackRow(source, label, badge);
                trackRow.ToggleMuteCommand = new RelayCommand(() => ToggleTrackMuteAsync(trackRow));
                trackRow.ToggleIncludeCommand = new RelayCommand(() => ToggleTrackIncludeAsync(trackRow));
                PlayerTracks.Add(trackRow);
            }
            _playerTimer.Start();
            ((RelayCommand)PlayerPlayPauseCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PlayerStopCommand).RaiseCanExecuteChanged();
            // Seek each source to its offset-aligned start, then play.
            await player.PlayAlignedAsync();
            // All sources are up; hide the loading overlay in the tiled window.
            tiledWindow?.SetLoadingComplete();
        }
        catch (Exception ex)
        {
            _playerError = ex.Message;
            Status = $"Playback failed: {ex.Message}";
            await UnloadPlayerAsync();
            Raise(nameof(PlayerPlaceholderText));
        }
    }

    private async Task UnloadPlayerAsync()
    {
        _playerTimer?.Stop();
        PlayerTracks.Clear();
        var player = _player;
        _player = null;
        Raise(nameof(IsMultiWindow));
        var tiledWindow = _tiledWindow;
        _tiledWindow = null;
        _playerTake = null;
        _playerSceneId = null;
        _playerDisplayName = null;
        NowPlayingText = "";
        PlayerCaptionText = "";
        if (player is not null)
            // No ConfigureAwait(false): everything below touches UI-bound state.
            await player.DisposeAsync();
        // Close the tiled window (if the user closed it first, this is a no-op).
        try { tiledWindow?.Close(); } catch { }
        PlayerPositionSeconds = 0;
        PlayerDurationSeconds = 0;
        IsPlayerPlaying = false;
        Raise(nameof(HasPlayerTake));
        if (_playerError is null) Raise(nameof(PlayerPlaceholderText));
        if (PlayerPlayPauseCommand is not null)
            ((RelayCommand)PlayerPlayPauseCommand).RaiseCanExecuteChanged();
        if (PlayerStopCommand is not null)
            ((RelayCommand)PlayerStopCommand).RaiseCanExecuteChanged();
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
            // Seek reliably: pause, seek, then resume if it was playing.
            // (Seeking while playing doesn't take effect on some mpv builds.)
            var wasPlaying = _player.IsPlaying;
            if (wasPlaying) await _player.PauseAsync();
            await _player.SeekAsync(TimeSpan.FromSeconds(seconds));
            PlayerPositionSeconds = seconds;
            if (wasPlaying) await _player.PlayAsync();
        }
        catch (Exception ex) { Status = $"Seek: {ex.Message}"; }
    }

    private async void ToggleTrackMuteAsync(PlayerTrackRow row)
    {
        if (_player is not TiledTakePlayer player) return;
        var muting = !row.IsMuted;
        if (muting && PlayerTracks.All(t => t == row || t.IsMuted))
        {
            Status = "Can't mute every track in the take.";
            return;
        }
        try
        {
            await player.SetSourceMutedAsync(row.SourceId, muting);
            row.IsMuted = muting;
            _tiledWindow?.SetTileMuted(row.SourceId, muting);
        }
        catch (Exception ex) { Status = $"Mute: {ex.Message}"; }
    }

    private async void ToggleTrackIncludeAsync(PlayerTrackRow row)
    {
        if (_player is not TiledTakePlayer player) return;
        var including = !row.IsIncluded;
        try
        {
            await player.SetSourceIncludedAsync(row.SourceId, including);
            row.IsIncluded = including;
            _tiledWindow?.SetTileVisible(row.SourceId, including);
        }
        catch (Exception ex) { Status = $"Include: {ex.Message}"; }
    }

}
