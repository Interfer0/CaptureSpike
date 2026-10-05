using System.Text.Json.Serialization;

namespace CaptureSpike.Core;

/// <summary>Kind of capture source. Video kinds produce a video file,
/// audio kinds produce an audio file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceKind
{
    Screen,
    Window,
    Webcam,
    Microphone,
    SystemAudio,
}

public static class SourceKindExtensions
{
    public static bool IsAudio(this SourceKind kind) =>
        kind is SourceKind.Microphone or SourceKind.SystemAudio;

    public static bool IsVideo(this SourceKind kind) => !kind.IsAudio();

    /// <summary>Two-letter badge shown in the UI, e.g. SC, WI, WE, MI, SY.</summary>
    public static string Badge(this SourceKind kind) => kind switch
    {
        SourceKind.Screen => "SC",
        SourceKind.Window => "WI",
        SourceKind.Webcam => "WE",
        SourceKind.Microphone => "MI",
        SourceKind.SystemAudio => "SY",
        _ => "??",
    };

    public static string DefaultLabel(this SourceKind kind) => kind switch
    {
        SourceKind.Screen => "Screen",
        SourceKind.Window => "Window",
        SourceKind.Webcam => "Webcam",
        SourceKind.Microphone => "Microphone",
        SourceKind.SystemAudio => "System Audio",
        _ => kind.ToString(),
    };
}

/// <summary>Output settings for the whole project.</summary>
public sealed class Project
{
    public string Name { get; set; } = "Untitled Project";
    public int OutputWidth { get; set; } = 1920;
    public int OutputHeight { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    /// <summary>
    /// The project's reusable source palette. Scenes reference these by id;
    /// takes record which palette entries they captured.
    /// </summary>
    public List<ProjectSource> Sources { get; set; } = new();

    public static List<ProjectSource> DefaultPalette() =>
    [
        new ProjectSource { Id = "screen", Kind = SourceKind.Screen, Label = "Screen" },
        new ProjectSource { Id = "webcam", Kind = SourceKind.Webcam, Label = "Webcam" },
        new ProjectSource { Id = "mic", Kind = SourceKind.Microphone, Label = "Microphone" },
        new ProjectSource { Id = "system", Kind = SourceKind.SystemAudio, Label = "System Audio" },
    ];
}

/// <summary>
/// One reusable capture device in the project palette: a screen, a window,
/// a camera, a microphone, or a system-audio loopback.
/// </summary>
public sealed class ProjectSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public SourceKind Kind { get; set; } = SourceKind.Screen;
    public string Label { get; set; } = "Screen";
    /// <summary>Device selector, e.g. window title, camera name. Null = default.</summary>
    public string? Device { get; set; }
}

/// <summary>
/// A storyboard segment: one planned chunk of the tutorial (e.g. "Intro",
/// "Install the tools"). Holds an ordered set of takes; exactly one take may
/// be starred as the favorite used by Export.
/// </summary>
public sealed class Scene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = "New Scene";
    public int Order { get; set; }
    /// <summary>Teleprompter notes shown while recording this scene.</summary>
    public string Notes { get; set; } = string.Empty;
    public string? FavoriteTakeId { get; set; }
    /// <summary>Palette sources assigned to this scene.</summary>
    public List<SceneSource> Sources { get; set; } = new();

    /// <summary>
    /// New scene wired to the project's palette: full-bleed screen, webcam
    /// picture-in-picture, mic and system audio.
    /// </summary>
    public static Scene Create(string title, int order, Project project)
    {
        var scene = new Scene { Title = title, Order = order };

        SceneSource? Ref(string paletteId, double x, double y, double w, double h)
        {
            var want = paletteId switch
            {
                "screen" => SourceKind.Screen,
                "webcam" => SourceKind.Webcam,
                "mic" => SourceKind.Microphone,
                "system" => SourceKind.SystemAudio,
                _ => (SourceKind?)null,
            };
            var def = project.Sources.FirstOrDefault(p => p.Id == paletteId)
                ?? (want.HasValue ? project.Sources.FirstOrDefault(p => p.Kind == want.Value) : null);
            if (def is null) return null;
            return new SceneSource { SourceId = def.Id, X = x, Y = y, Width = w, Height = h };
        }

        foreach (var id in new[] { "screen", "webcam", "mic", "system" })
        {
            var layout = id switch
            {
                "screen" => (0.0, 0.0, 1.0, 1.0),
                "webcam" => (0.72, 0.70, 0.26, 0.26),
                _ => (0.0, 0.0, 1.0, 1.0),
            };
            var r = Ref(id, layout.Item1, layout.Item2, layout.Item3, layout.Item4);
            if (r is not null) scene.Sources.Add(r);
        }
        return scene;
    }
}

/// <summary>
/// A palette source assigned to a scene. Clicking the assignment toggles
/// <see cref="Enabled"/> for the next take; removing it takes it out of the
/// scene (the palette entry itself is untouched).
/// </summary>
public sealed class SceneSource
{
    /// <summary>References <see cref="ProjectSource.Id"/> in the project palette.</summary>
    public string SourceId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    /// <summary>Normalized rect (0..1) within the output frame. Ignored for audio.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
}

/// <summary>
/// One recording pass of a scene. Each enabled source is stored as its own
/// file so levels/framing can be fixed after the fact.
/// </summary>
public sealed class Take
{
    /// <summary>Folder name, e.g. "20261004-223015". Unique within the scene.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>
    /// Stable per-scene sequence number ("Take 05"). Assigned as max+1 when the
    /// take is saved, so deleting a take leaves a gap instead of renumbering.
    /// </summary>
    public int Number { get; set; }
    public string SceneId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public List<TakeSource> Sources { get; set; } = new();
}

/// <summary>One recorded source file inside a take.</summary>
public sealed class TakeSource
{
    /// <summary>
    /// Matches the <see cref="ProjectSource.Id"/> palette entry this was recorded
    /// from. The take keeps its own copy so it stays readable even if the
    /// palette entry is later renamed or deleted.
    /// </summary>
    public string Id { get; set; } = string.Empty;
    public SourceKind Kind { get; set; }
    /// <summary>File name relative to the take folder.</summary>
    public string File { get; set; } = string.Empty;
    /// <summary>
    /// Milliseconds after the shared t0 when this source's first data arrived.
    /// Playback and export align tracks by these offsets — never assume zero.
    /// </summary>
    public double OffsetMs { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
