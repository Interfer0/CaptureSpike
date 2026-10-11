using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CaptureSpike.App;

/// <summary>
/// Settings dialog. The viewmodel handles persistence and live-applies the
/// theme choice so the user sees the change before clicking Save (themes
/// apply instantly across all open windows).
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext!;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose projects folder" });
        if (folders.Count > 0)
            Vm.ProjectsFolder = folders[0].Path.LocalPath;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Vm.Save();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// ViewModel for the settings dialog. The ComboBox for theme is bound
/// directly to the persisted choice so changing it writes through to
/// AppSettings and updates Avalonia's RequestedThemeVariant live.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public sealed record ThemeChoice(string Value, string Display);

    public SettingsViewModel()
    {
        _projectsFolder = AppSettings.ProjectsDir;
        _silenceThresholdDb = AppSettings.SilenceThresholdDb;
        _silenceMinDurationSec = AppSettings.SilenceMinDurationSec;
        ThemeOptions =
        [
            new ThemeChoice("Dark", "Dark"),
            new ThemeChoice("Light", "Light"),
            new ThemeChoice("Default", "System default (follow OS)"),
        ];
        // Match whatever App.OnFrameworkInitializationCompleted set on
        // startup. The default "Dark" was set by App if the settings
        // file was missing; otherwise the saved choice is preserved.
        var current = Avalonia.Application.Current?.RequestedThemeVariant;
        var choiceValue = current is { } v ? ThemeKeys.FromVariant(v) : "Dark";
        _theme = AppSettings.Theme ?? choiceValue;
        _themeSelection = ThemeOptions.FirstOrDefault(o => o.Value == _theme) ?? ThemeOptions[0];
        ApplyTheme();
    }

    private string _projectsFolder = "";
    public string ProjectsFolder
    {
        get => _projectsFolder;
        set => SetField(ref _projectsFolder, value);
    }

    // Audio level (in dB) below which a sample is considered
    // silence. Used by the normalize pass's trailing-silence
    // detector. Default -30dB. Range -60 to -10.
    private double _silenceThresholdDb;
    public double SilenceThresholdDb
    {
        get => _silenceThresholdDb;
        set
        {
            // Clamp on input so the TextBox can't show values
            // outside the supported range.
            var clamped = Math.Max(-60.0, Math.Min(-10.0, value));
            if (SetField(ref _silenceThresholdDb, clamped)) { }
        }
    }

    // Minimum duration of audio above the silence threshold
    // before it counts as a sound event. Default 0.05s
    // (50ms). Range 0.01 to 1.0.
    private double _silenceMinDurationSec;
    public double SilenceMinDurationSec
    {
        get => _silenceMinDurationSec;
        set
        {
            var clamped = Math.Max(0.01, Math.Min(1.0, value));
            if (SetField(ref _silenceMinDurationSec, clamped)) { }
        }
    }

    public IReadOnlyList<ThemeChoice> ThemeOptions { get; }

    private ThemeChoice _themeSelection = null!;
    private string _theme = "Dark";

    public ThemeChoice Theme
    {
        get => _themeSelection;
        set
        {
            if (!SetField(ref _themeSelection, value)) return;
            _theme = value.Value;
            ApplyTheme();
        }
    }

    /// <summary>Persists the projects folder and theme on Save.</summary>
    public void Save()
    {
        if (!string.IsNullOrWhiteSpace(_projectsFolder))
            AppSettings.ProjectsDir = _projectsFolder;
        AppSettings.Theme = _theme;
        AppSettings.SilenceThresholdDb = _silenceThresholdDb;
        AppSettings.SilenceMinDurationSec = _silenceMinDurationSec;
    }

    /// <summary>Apply the current theme to Avalonia. Called on construction
    /// and whenever the user changes the theme picker. Live-applies so the
    /// user sees the result without dismissing the dialog.</summary>
    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = ThemeKeys.ToVariant(_theme);
    }
}