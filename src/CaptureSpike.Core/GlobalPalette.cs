namespace CaptureSpike.Core;

/// <summary>
/// One reusable capture device available across every project — a screen,
/// a webcam, a microphone, or a system-audio loopback. The <see cref="Device"/>
/// field on a palette entry is the *default* device the user picked last
/// time; each project can override it per-project via
/// <see cref="Project.SourceOverrides"/>.
/// </summary>
public sealed class GlobalPalette
{
    public List<ProjectSource> Sources { get; set; } = new();

    /// <summary>
    /// Path of the global palette file. Re-resolves from environment
    /// variables on every call so test suites that override APPDATA
    /// mid-run still see their override.
    /// </summary>
    public static string FilePath => ResolveFilePath();

    private static string ResolveFilePath()
    {
        // Same conventions as AppSettings / AppLog: a single config
        // directory per user, holding palette.json at the top level.
        // Honour APPDATA on Windows and XDG_DATA_HOME on Linux/macOS
        // for testability.
        var explicitDir = System.Environment.GetEnvironmentVariable("CAPTURESPIKE_PALETTE_DIR");
        if (!string.IsNullOrEmpty(explicitDir))
        {
            Directory.CreateDirectory(explicitDir);
            return Path.Combine(explicitDir, "palette.json");
        }
        var baseDir = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.ApplicationData);
        return Path.Combine(baseDir, "CaptureSpike", "palette.json");
    }

    /// <summary>
    /// Loads the global palette from disk, or returns a fresh one
    /// seeded with the default palette if no file exists yet.
    /// </summary>
    public static GlobalPalette Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return new GlobalPalette
            {
                Sources = new List<ProjectSource>(Project.DefaultPalette()),
            };
        }
        try
        {
            var json = File.ReadAllText(path);
            var palette = System.Text.Json.JsonSerializer.Deserialize<GlobalPalette>(json);
            if (palette is not null) return palette;
        }
        catch { /* fall through to default */ }
        return new GlobalPalette
        {
            Sources = new List<ProjectSource>(Project.DefaultPalette()),
        };
    }

    /// <summary>Saves the palette to <see cref="FilePath"/> atomically.</summary>
    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Mirror the atomic-write pattern ProjectStore uses so a crash
        // mid-write doesn't leave a half-written palette.json.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(this,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>True if a source with the given id is already in the palette.</summary>
    public bool Contains(string id) => Sources.Any(s => s.Id == id);

    /// <summary>Adds a source. No-op if an entry with the same id already exists.</summary>
    public void Add(ProjectSource source)
    {
        if (Contains(source.Id)) return;
        Sources.Add(source);
    }

    /// <summary>Removes a source by id. Returns true if something was removed.</summary>
    public bool Remove(string id)
    {
        for (var i = 0; i < Sources.Count; i++)
        {
            if (Sources[i].Id == id)
            {
                Sources.RemoveAt(i);
                return true;
            }
        }
        return false;
    }
}
