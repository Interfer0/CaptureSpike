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
    /// On-disk naming scheme version. 0 = legacy (GUID scene folders,
    /// timestamp take folders, palette-id filenames); 1 = named folders
    /// and Project-Scene-Take-Badge-Label filenames.
    /// </summary>
    public int NamingVersion { get; set; }
    /// <summary>
    /// The project's reusable source palette. Each entry is the
    /// project's own copy of a kind — kind, label, badge, and the
    /// device the user picked for THIS project. Two projects can
    /// have a "Webcam" source with different device picks; that's
    /// intentional, the project owns its source config. The
    /// "master" <see cref="GlobalPalette"/> only tracks which kinds
    /// exist (so the user has something to pick from when adding a
    /// new kind); it does NOT override per-project choices.
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
    /// <summary>
    /// Custom short badge shown on chips (e.g. "M1", "MK"). Empty = the kind's
    /// default badge. Lets two sources of the same kind tell apart.
    /// </summary>
    public string Badge { get; set; } = "";

    /// <summary>Custom badge if set, otherwise the kind's default badge.</summary>
    public string EffectiveBadge() =>
        string.IsNullOrWhiteSpace(Badge) ? Kind.Badge() : Badge.Trim().ToUpperInvariant();
}

/// <summary>
/// A storyboard segment: one planned chunk of the tutorial (e.g. "Intro",
/// "Install the tools"). Holds an ordered set of takes; exactly one take may
/// be starred as the favorite used by Export.
/// </summary>
public sealed class Scene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>
    /// On-disk folder name under scenes/, derived from <see cref="Title"/>
    /// (sanitized, deduped). Updated when the scene is renamed; empty means
    /// "legacy GUID folder, migrate on open".
    /// </summary>
    public string FolderName { get; set; } = "";
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

        // Per-project palette. The global registry is the fallback for
        // kinds the project doesn't have yet (e.g., the user just added
        // a new webcam in another project and hasn't opened this one
        // since — they'd still see it on next open).
        var local = project.Sources;
        var registry = GlobalPalette.Load().Sources;

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
            var def = local.FirstOrDefault(p => p.Id == paletteId)
                ?? registry.FirstOrDefault(p => p.Id == paletteId)
                ?? (want.HasValue ? local.FirstOrDefault(p => p.Kind == want.Value) : null)
                ?? (want.HasValue ? registry.FirstOrDefault(p => p.Kind == want.Value) : null);
            if (def is null) return null;
            return new SceneSource { SourceId = def.Id, X = x, Y = y, Width = w, Height = h };
        }

        // Wire the minimum useful sources by default: screen (the main
        // capture) and mic (the spoken audio). Webcam and system audio
        // are optional extras — pre-wiring everything means the
        // "Sources in scene" add picker has nothing left to offer, so
        // new projects feel locked. The user can add the rest via the
        // scene's "+ Add" picker.
        foreach (var id in new[] { "screen", "mic" })
        {
            var layout = id switch
            {
                "screen" => (0.0, 0.0, 1.0, 1.0),
                _ => (0.0, 0.0, 1.0, 1.0), // audio is full-frame (no rect)
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
    /// <summary>Folder name, e.g. "Take 05" (or "Take 05-favorite"). Unique within the scene.</summary>
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
    /// This is the recorder's calculated value — never overwritten by the UI.
    /// Playback and export align tracks by <see cref="EffectiveOffsetMs"/>.
    /// </summary>
    public double OffsetMs { get; set; }

    /// <summary>
    /// User-adjusted offset in milliseconds. When set, playback uses this
    /// instead of <see cref="OffsetMs"/>. Null means "use the recorded value".
    /// </summary>
    public double? ManualOffsetMs { get; set; }

    /// <summary>The offset actually used for playback: manual if set, else recorded.</summary>
    public double EffectiveOffsetMs => ManualOffsetMs ?? OffsetMs;

    public int? Width { get; set; }
    public int? Height { get; set; }
}
