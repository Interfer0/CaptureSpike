using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptureSpike.Core;

/// <summary>
/// On-disk layout:
///   &lt;projectDir&gt;/project.json
///   &lt;projectDir&gt;/scenes/&lt;sceneId&gt;/scene.json
///   &lt;projectDir&gt;/scenes/&lt;sceneId&gt;/takes/&lt;takeId&gt;/take.json + media files
/// </summary>
public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Writes JSON atomically: temp file + rename, so a crash mid-write
    /// can't leave an empty or partial file behind.
    /// </summary>
    private static void WriteJsonAtomic(string path, string content)
    {
        // Unique temp name so concurrent writers (or a stale temp from a
        // crashed run) can never collide on the same path.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, content);
        try
        {
            // Retry the rename: on Windows, antivirus and cloud sync
            // (OneDrive/Dropbox, common on the Documents folder) can
            // briefly lock the destination file.
            const int attempts = 6;
            for (var i = 0; ; i++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (i == attempts - 1) throw;
                    Thread.Sleep(100 * (i + 1));
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public string ProjectDir { get; }
    public Project Project { get; private set; }

    private ProjectStore(string projectDir, Project project)
    {
        ProjectDir = projectDir;
        Project = project;
    }

    public static ProjectStore Create(string projectDir, string name)
    {
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "scenes"));
        var store = new ProjectStore(projectDir,
            new Project { Name = name, Sources = Project.DefaultPalette() });
        store.SaveProject();
        return store;
    }

    public static ProjectStore Open(string projectDir)
    {
        var json = File.ReadAllText(Path.Combine(projectDir, "project.json"));
        var project = JsonSerializer.Deserialize<Project>(json, Json)
            ?? throw new InvalidDataException("project.json is empty or invalid.");
        var store = new ProjectStore(projectDir, project);
        store.EnsurePalette();
        return store;
    }

    /// <summary>
    /// Projects written before the source palette get one: the default
    /// palette, plus any custom devices their scenes used. Their scenes are
    /// rewritten in the new reference format.
    /// </summary>
    private void EnsurePalette()
    {
        var dirty = false;
        if (Project.Sources.Count == 0)
        {
            Project.Sources = Project.DefaultPalette();
            dirty = true;
        }

        if (Directory.Exists(ScenesDir))
        {
            foreach (var dir in Directory.GetDirectories(ScenesDir))
                dirty |= MigrateLegacyScene(Path.GetFileName(dir));
        }

        // Don't rewrite project.json when nothing changed — avoids a
        // pointless write (and its failure modes) on every startup.
        if (dirty) SaveProject();
    }

    /// <summary>Legacy scene.json shape, before SceneSource became a palette reference.</summary>
    private sealed class LegacySceneSource
    {
        public string Id { get; set; } = string.Empty;
        public SourceKind Kind { get; set; }
        public bool Enabled { get; set; } = true;
        public string? Device { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 1;
        public double Height { get; set; } = 1;
    }

    private bool MigrateLegacyScene(string sceneId)
    {
        var path = Path.Combine(SceneDir(sceneId), "scene.json");
        if (!File.Exists(path)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            return false;
        var first = sources.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object || first.TryGetProperty("sourceId", out _))
            return false; // already in the new format (or no sources)

        var legacy = JsonSerializer.Deserialize<List<LegacySceneSource>>(sources.GetRawText(), Json);
        if (legacy is null) return false;
        var scene = JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Json);
        if (scene is null) return false;

        scene.Sources.Clear();
        foreach (var ls in legacy)
        {
            var palette = Project.Sources.FirstOrDefault(p => p.Id == ls.Id);
            if (palette is null)
            {
                palette = new ProjectSource
                {
                    Id = ls.Id, Kind = ls.Kind,
                    Label = ls.Kind.DefaultLabel(), Device = ls.Device,
                };
                Project.Sources.Add(palette);
            }
            scene.Sources.Add(new SceneSource
            {
                SourceId = palette.Id,
                Enabled = ls.Enabled,
                X = ls.X, Y = ls.Y, Width = ls.Width, Height = ls.Height,
            });
        }
        SaveScene(scene);
        return true;
    }

    public void SaveProject() =>
        WriteJsonAtomic(Path.Combine(ProjectDir, "project.json"),
            JsonSerializer.Serialize(Project, Json));

    private string ScenesDir => Path.Combine(ProjectDir, "scenes");
    private string SceneDir(string sceneId) => Path.Combine(ScenesDir, sceneId);
    private string TakesDir(string sceneId) => Path.Combine(SceneDir(sceneId), "takes");

    // ----- scenes -----

    public IReadOnlyList<Scene> LoadScenes()
    {
        if (!Directory.Exists(ScenesDir)) return [];
        return Directory.GetDirectories(ScenesDir)
            .Select(d => LoadScene(Path.GetFileName(d)))
            .Where(s => s is not null)
            .Cast<Scene>()
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Id)
            .ToList();
    }

    private Scene? LoadScene(string sceneId)
    {
        var path = Path.Combine(SceneDir(sceneId), "scene.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Json);
    }

    public Scene AddScene(string title)
    {
        var order = LoadScenes().Count;
        var scene = Scene.Create(title, order, Project);
        Directory.CreateDirectory(TakesDir(scene.Id));
        SaveScene(scene);
        return scene;
    }

    public void SaveScene(Scene scene) =>
        WriteJsonAtomic(Path.Combine(SceneDir(scene.Id), "scene.json"),
            JsonSerializer.Serialize(scene, Json));

    public void DeleteScene(Scene scene)
    {
        var dir = SceneDir(scene.Id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        // Re-compact ordering.
        var i = 0;
        foreach (var s in LoadScenes()) { s.Order = i++; SaveScene(s); }
    }

    public void MoveScene(Scene scene, int newOrder)
    {
        var scenes = LoadScenes().ToList();
        scenes.RemoveAll(s => s.Id == scene.Id);
        newOrder = Math.Clamp(newOrder, 0, scenes.Count);
        scenes.Insert(newOrder, scene);
        var i = 0;
        foreach (var s in scenes) { s.Order = i++; SaveScene(s); }
    }

    // ----- takes -----

    public IReadOnlyList<Take> LoadTakes(string sceneId)
    {
        var dir = TakesDir(sceneId);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir)
            .Select(d => LoadTake(sceneId, Path.GetFileName(d)))
            .Where(t => t is not null)
            .Cast<Take>()
            .OrderBy(t => t.StartedAt)
            .ToList();
    }

    private Take? LoadTake(string sceneId, string takeId)
    {
        var path = Path.Combine(TakesDir(sceneId), takeId, "take.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Take>(File.ReadAllText(path), Json);
    }

    /// <summary>Reserves a new take folder and returns its path.</summary>
    public string BeginTake(Scene scene)
    {
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(TakesDir(scene.Id), id);
        for (var n = 2; Directory.Exists(dir); n++)
            dir = Path.Combine(TakesDir(scene.Id), $"{id}-{n}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void SaveTake(string takeDir, Take take) =>
        WriteJsonAtomic(Path.Combine(takeDir, "take.json"),
            JsonSerializer.Serialize(take, Json));

    public void DeleteTake(string sceneId, Take take)
    {
        var dir = Path.Combine(TakesDir(sceneId), take.Id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        foreach (var s in LoadScenes().Where(s => s.Id == sceneId && s.FavoriteTakeId == take.Id))
        {
            s.FavoriteTakeId = null;
            SaveScene(s);
        }
    }

    public string TakeDir(string sceneId, string takeId) =>
        Path.Combine(TakesDir(sceneId), takeId);

    /// <summary>
    /// Renames a take's folder (used for the "-favorite" suffix). Returns the
    /// id actually in effect: if the move fails the old id is returned.
    /// </summary>
    public string RenameTake(string sceneId, string takeId, string newId)
    {
        if (takeId == newId) return takeId;
        var from = TakeDir(sceneId, takeId);
        var to = TakeDir(sceneId, newId);
        try
        {
            if (!Directory.Exists(from) || Directory.Exists(to)) return takeId;
            Directory.Move(from, to);
            // Keep take.json's Id in sync with the folder name.
            var take = LoadTake(sceneId, newId);
            if (take is not null)
            {
                take.Id = newId;
                WriteJsonAtomic(Path.Combine(to, "take.json"),
                    JsonSerializer.Serialize(take, Json));
            }
            return newId;
        }
        catch
        {
            return takeId;
        }
    }
}
