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
}

/// <summary>Output settings for the whole project.</summary>
public sealed class Project
{
    public string Name { get; set; } = "Untitled Project";
    public int OutputWidth { get; set; } = 1920;
    public int OutputHeight { get; set; } = 1080;
    public int Fps { get; set; } = 30;
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
    public List<SceneSource> Sources { get; set; } = new();

    public static Scene WithDefaultSources(string title, int order) => new()
    {
        Title = title,
        Order = order,
        Sources =
        [
            new SceneSource { Id = "screen", Kind = SourceKind.Screen, X = 0, Y = 0, Width = 1, Height = 1 },
            new SceneSource { Id = "webcam", Kind = SourceKind.Webcam, X = 0.72, Y = 0.70, Width = 0.26, Height = 0.26 },
            new SceneSource { Id = "mic", Kind = SourceKind.Microphone },
            new SceneSource { Id = "system", Kind = SourceKind.SystemAudio },
        ],
    };
}

/// <summary>A source enabled for a scene, with its compositing layout.</summary>
public sealed class SceneSource
{
    public string Id { get; set; } = "screen";
    public SourceKind Kind { get; set; } = SourceKind.Screen;
    public bool Enabled { get; set; } = true;
    /// <summary>Device selector, e.g. window title, camera name. Null = default.</summary>
    public string? Device { get; set; }
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
    public string SceneId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public List<TakeSource> Sources { get; set; } = new();
}

/// <summary>One recorded source file inside a take.</summary>
public sealed class TakeSource
{
    /// <summary>Matches the SceneSource.Id this was recorded from.</summary>
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
