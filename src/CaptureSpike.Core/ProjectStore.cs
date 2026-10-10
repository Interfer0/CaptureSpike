using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptureSpike.Core;

/// <summary>
/// On-disk layout (all names Resolve-friendly, derived from UI names):
///   &lt;projectDir&gt;/project.json
///   &lt;projectDir&gt;/scenes/&lt;Scene Title&gt;/scene.json
///   &lt;projectDir&gt;/scenes/&lt;Scene Title&gt;/takes/&lt;Take 05&gt;/take.json + media files
/// Media files: &lt;Project&gt;-&lt;Scene&gt;-&lt;Take 05&gt;-&lt;Badge&gt;-&lt;Source Label&gt;.&lt;ext&gt;
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
    /// Writes JSON atomically: temp file + rename with retry on transient
    /// locks (Windows AV, OneDrive / Dropbox). See <see cref="AtomicFile"/>
    /// for the shared implementation.
    /// </summary>
    private static void WriteJsonAtomic(string path, string content) =>
        AtomicFile.WriteAllText(path, content);

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
        // New projects don't seed their own palette — sources live in
        // the global palette (GlobalPalette.json). The SourceOverrides
        // list is left empty; the user picks devices per-source as
        // needed and they're saved there.
        var store = new ProjectStore(projectDir,
            new Project { Name = name, Sources = new List<ProjectSource>(), NamingVersion = 1 });
        store.SaveProject();
        return store;
    }

    public static ProjectStore Open(string projectDir)
    {
        var json = File.ReadAllText(Path.Combine(projectDir, "project.json"));
        var project = JsonSerializer.Deserialize<Project>(json, Json)
            ?? throw new InvalidDataException("project.json is empty or invalid.");
        var store = new ProjectStore(projectDir, project);
        // Migrate legacy scene sources (embedded in scene.json) to palette
        // references BEFORE renaming folders: MigrateToNamedFolders saves
        // each scene.json, and a saved scene with empty sourceId would
        // be mistaken for "already migrated" by MigrateLegacyScene.
        store.EnsurePalette();
        store.MigrateToGlobalPalette();
        if (project.NamingVersion < 1)
        {
            store.MigrateToNamedFolders();
            project.NamingVersion = 1;
            store.SaveProject();
        }
        return store;
    }

    /// <summary>
    /// One-time migration of the per-project palette into the global
    /// palette. Any <see cref="ProjectSource"/> in this project's
    /// <c>Sources</c> is added to the global palette (dedup by id;
    /// existing entries are preserved). Any per-source <c>Device</c>
    /// and <c>Badge</c> selections are preserved as
    /// <see cref="SourceOverride"/> entries on this project so the
    /// user keeps the device and badge they had picked. Safe to call
    /// repeatedly — once a project has empty <c>Sources</c> AND the
    /// global palette contains all of them, it's a no-op.
    /// </summary>
    private void MigrateToGlobalPalette()
    {
        if (Project.Sources.Count == 0) return;
        var palette = GlobalPalette.Load();
        var changed = false;
        foreach (var src in Project.Sources)
        {
            if (!palette.Contains(src.Id))
            {
                palette.Add(src);
                changed = true;
            }
            // Preserve any per-source device / badge / label selection
            // as an override. These are project-specific (different
            // projects might pick different devices, badges, or
            // labels for the same palette id), so they stay out of
            // the global palette.
            var existing = Project.SourceOverrides.FirstOrDefault(o => o.Id == src.Id);
            if (existing is null &&
                (!string.IsNullOrEmpty(src.Device)
                 || !string.IsNullOrWhiteSpace(src.Badge)
                 || !string.IsNullOrWhiteSpace(src.Label)))
            {
                Project.SourceOverrides.Add(new SourceOverride
                {
                    Id = src.Id, Device = src.Device, Badge = src.Badge, Label = src.Label,
                });
            }
            else if (existing is not null)
            {
                if (!string.IsNullOrEmpty(src.Device) && existing.Device != src.Device)
                    existing.Device = src.Device;
                if (!string.IsNullOrWhiteSpace(src.Badge) && existing.Badge != src.Badge)
                    existing.Badge = src.Badge;
                if (!string.IsNullOrWhiteSpace(src.Label) && existing.Label != src.Label)
                    existing.Label = src.Label;
            }
        }
        if (changed) palette.Save();
        // Clear the now-redundant per-project palette. The field is
        // still deserialized for backward compat, but the UI loads
        // from the global palette instead.
        Project.Sources = new List<ProjectSource>();
        SaveProject();
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

    /// <summary>
    /// One-way migration from the old on-disk layout to the named one:
    /// GUID scene folders → title folders, timestamp take folders →
    /// "Take NN", palette-id media files → Project-Scene-Take-Badge-Label
    /// names. Idempotent: already-migrated items are skipped.
    /// </summary>
    private void MigrateToNamedFolders()
    {
        if (!Directory.Exists(ScenesDir)) return;
        foreach (var dir in Directory.GetDirectories(ScenesDir))
        {
            var dirName = Path.GetFileName(dir);
            Scene? scene;
            try
            {
                var json = File.ReadAllText(Path.Combine(dir, "scene.json"));
                scene = JsonSerializer.Deserialize<Scene>(json, Json);
            }
            catch { continue; }
            if (scene is null) continue;

            var folderName = dirName;
            if (string.IsNullOrEmpty(scene.FolderName))
            {
                // Legacy GUID folder → title-based folder.
                folderName = Naming.UniqueName(Naming.SanitizeFileName(scene.Title),
                    n => Directory.Exists(Path.Combine(ScenesDir, n)));
                if (!folderName.Equals(dirName, StringComparison.Ordinal))
                {
                    try { Directory.Move(dir, Path.Combine(ScenesDir, folderName)); }
                    catch { continue; } // try again next open
                }
                scene.FolderName = folderName;
            }
            else
            {
                scene.FolderName = dirName;
                folderName = dirName;
            }

            var remap = MigrateTakeFolders(folderName, scene);
            if (scene.FavoriteTakeId is not null &&
                remap.TryGetValue(scene.FavoriteTakeId, out var newFav))
                scene.FavoriteTakeId = newFav;
            SaveScene(scene);
        }
    }

    /// <summary>
    /// Renames timestamp take folders to "Take NN" (preserving "-favorite")
    /// and media files to the Resolve-friendly pattern. Returns old→new
    /// take-id mapping for FavoriteTakeId repair.
    /// </summary>
    private Dictionary<string, string> MigrateTakeFolders(string folderName, Scene scene)
    {
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var takesDir = TakesDir(folderName);
        if (!Directory.Exists(takesDir)) return remap;

        // Assign numbers to takes that predate stable numbering.
        var nextNum = 0;
        foreach (var dir in Directory.GetDirectories(takesDir))
        {
            var take = LoadTakeFromDir(dir);
            if (take is not null && take.Number > nextNum) nextNum = take.Number;
        }

        foreach (var dir in Directory.GetDirectories(takesDir))
        {
            var oldName = Path.GetFileName(dir);
            Take? take;
            try
            {
                var json = File.ReadAllText(Path.Combine(dir, "take.json"));
                take = JsonSerializer.Deserialize<Take>(json, Json);
            }
            catch { continue; }
            if (take is null) continue;
            if (take.Number <= 0) take.Number = ++nextNum;

            var newBase = $"Take {take.Number:D2}";
            string newName;
            string takeDir = dir;
            if (oldName.StartsWith("Take ", StringComparison.Ordinal))
            {
                newName = oldName; // already migrated
            }
            else
            {
                var suffix = oldName.EndsWith("-favorite", StringComparison.Ordinal) ? "-favorite" : "";
                newName = Naming.UniqueName(newBase + suffix,
                    n => Directory.Exists(Path.Combine(takesDir, n)));
                if (!newName.Equals(oldName, StringComparison.Ordinal))
                {
                    try { Directory.Move(dir, Path.Combine(takesDir, newName)); }
                    catch { continue; } // try again next open
                    takeDir = Path.Combine(takesDir, newName);
                }
                take.Id = newName;
            }
            remap[oldName] = newName;
            MigrateTakeFiles(scene, take, takeDir, newBase);
            SaveTake(takeDir, take);
        }
        return remap;
    }

    private Take? LoadTakeFromDir(string dir)
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(dir, "take.json"));
            return JsonSerializer.Deserialize<Take>(json, Json);
        }
        catch { return null; }
    }

    /// <summary>
    /// Renames a take's media files to Project-Scene-Take-Badge-Label.ext.
    /// </summary>
    private void MigrateTakeFiles(Scene scene, Take take, string takeDir, string takeBaseName)
    {
        var dirty = false;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ts in take.Sources)
        {
            var (baseName, ext) = MediaFileBase(scene.Title, takeBaseName, ts);
            var newFile = baseName + ext;
            for (var i = 2;
                 used.Contains(newFile) ||
                 (!newFile.Equals(ts.File, StringComparison.OrdinalIgnoreCase) &&
                  File.Exists(Path.Combine(takeDir, newFile)));
                 i++)
                newFile = $"{baseName} {i}{ext}";
            used.Add(newFile);
            if (newFile.Equals(ts.File, StringComparison.OrdinalIgnoreCase)) continue;
            var from = Path.Combine(takeDir, ts.File);
            var to = Path.Combine(takeDir, newFile);
            if (File.Exists(from) && !File.Exists(to))
            {
                try { File.Move(from, to); ts.File = newFile; dirty = true; }
                catch { /* try again next open */ }
            }
            else if (!File.Exists(from) && File.Exists(to))
            {
                // Already renamed by an interrupted run: fix the reference.
                ts.File = newFile; dirty = true;
            }
        }
        if (dirty) SaveTake(takeDir, take);
    }

    /// <summary>
    /// Resolve-friendly media name: Project-Scene-Take 05-Badge-Label.ext
    /// </summary>
    public string MediaFileName(string sceneTitle, string takeBaseName, TakeSource ts)
    {
        var (baseName, ext) = MediaFileBase(sceneTitle, takeBaseName, ts);
        return baseName + ext;
    }

    private (string baseName, string ext) MediaFileBase(string sceneTitle, string takeBaseName, TakeSource ts)
    {
        // Look up the badge and label in the global palette, with
        // per-project overrides taking precedence. The Sources field
        // on the project is empty in the post-migration model; the
        // global palette is the source of truth.
        var global = GlobalPalette.Load();
        var palette = global.Sources.FirstOrDefault(p => p.Id == ts.Id);
        var ovr = Project.SourceOverrides.FirstOrDefault(o => o.Id == ts.Id);
        var badge = !string.IsNullOrWhiteSpace(ovr?.Badge)
            ? ovr!.Badge!.Trim().ToUpperInvariant()
            : palette?.EffectiveBadge() ?? ts.Kind.Badge();
        var label = !string.IsNullOrWhiteSpace(ovr?.Label) ? ovr!.Label! :
                    palette?.Label ?? ts.Kind.DefaultLabel();
        var ext = Path.GetExtension(ts.File);
        if (string.IsNullOrEmpty(ext)) ext = ts.Kind.IsVideo() ? ".mkv" : ".wav";
        var baseName = string.Join("-", new[]
        {
            Naming.SanitizeFileName(Project.Name),
            Naming.SanitizeFileName(sceneTitle),
            takeBaseName,
            Naming.SanitizeFileName(badge),
            Naming.SanitizeFileName(label),
        });
        return (baseName, ext);
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

    private bool MigrateLegacyScene(string folderName)
    {
        var path = Path.Combine(SceneDir(folderName), "scene.json");
        if (!File.Exists(path)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            return false;
        var first = sources.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object || first.TryGetProperty("sourceId", out _))
        {
            // Already migrated only if every source has a non-empty sourceId.
            // A partially-migrated scene (e.g. one that MigrateToNamedFolders
            // saved before MigrateLegacyScene ran) still needs finishing.
            var hasEmptyId = false;
            foreach (var s in sources.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object ||
                    !s.TryGetProperty("sourceId", out var sid) ||
                    sid.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(sid.GetString()))
                {
                    hasEmptyId = true;
                    break;
                }
            }
            if (!hasEmptyId) return false;
        }

        var legacy = JsonSerializer.Deserialize<List<LegacySceneSource>>(sources.GetRawText(), Json);
        if (legacy is null) return false;
        var scene = JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Json);
        if (scene is null) return false;
        if (string.IsNullOrEmpty(scene.FolderName)) scene.FolderName = folderName;

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
    private string SceneDir(string folderName) => Path.Combine(ScenesDir, folderName);
    private string TakesDir(string folderName) => Path.Combine(SceneDir(folderName), "takes");

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

    private Scene? LoadScene(string folderName)
    {
        var path = Path.Combine(SceneDir(folderName), "scene.json");
        if (!File.Exists(path)) return null;
        var scene = JsonSerializer.Deserialize<Scene>(File.ReadAllText(path), Json);
        // The directory name is the source of truth; self-heals a manual
        // rename outside the app.
        if (scene is not null && scene.FolderName != folderName)
        {
            scene.FolderName = folderName;
            try
            {
                WriteJsonAtomic(path, JsonSerializer.Serialize(scene, Json));
            }
            catch { /* best-effort */ }
        }
        return scene;
    }

    public Scene AddScene(string title)
    {
        var order = LoadScenes().Count;
        var scene = Scene.Create(title, order, Project);
        scene.FolderName = Naming.UniqueName(Naming.SanitizeFileName(title),
            n => Directory.Exists(Path.Combine(ScenesDir, n)));
        Directory.CreateDirectory(TakesDir(scene.FolderName));
        SaveScene(scene);
        return scene;
    }

    public void SaveScene(Scene scene) =>
        WriteJsonAtomic(Path.Combine(SceneDir(scene.FolderName), "scene.json"),
            JsonSerializer.Serialize(scene, Json));

    public void DeleteScene(Scene scene)
    {
        var dir = SceneDir(scene.FolderName);
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

    /// <summary>
    /// Renames a scene: the folder follows the new title (sanitized, deduped),
    /// and media files in every take are renamed to match (their names embed
    /// the scene title). Returns false and leaves everything untouched if the
    /// folder rename failed.
    /// </summary>
    public bool RenameScene(Scene scene, string newTitle)
    {
        newTitle = (newTitle ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newTitle)) newTitle = "New Scene";
        if (newTitle == scene.Title) return true;
        var want = Naming.UniqueName(Naming.SanitizeFileName(newTitle),
            n => !n.Equals(scene.FolderName, StringComparison.OrdinalIgnoreCase)
                 && Directory.Exists(Path.Combine(ScenesDir, n)));
        if (!want.Equals(scene.FolderName, StringComparison.Ordinal))
        {
            try
            {
                var oldDir = SceneDir(scene.FolderName);
                if (Directory.Exists(oldDir))
                    Directory.Move(oldDir, Path.Combine(ScenesDir, want));
                scene.FolderName = want;
            }
            catch
            {
                return false;
            }
        }
        scene.Title = newTitle;
        SaveScene(scene);
        // Media filenames embed the scene title: rename them to match.
        foreach (var take in LoadTakes(scene.FolderName))
        {
            var takeDir = TakeDir(scene.FolderName, take.Id);
            var takeBase = TakeNumberFromFolder(take.Id) is int n ? $"Take {n:D2}" : take.Id;
            MigrateTakeFiles(scene, take, takeDir, takeBase);
        }
        return true;
    }

    // ----- takes -----

    public IReadOnlyList<Take> LoadTakes(string folderName)
    {
        var dir = TakesDir(folderName);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir)
            .Select(d => LoadTake(folderName, Path.GetFileName(d)))
            .Where(t => t is not null)
            .Cast<Take>()
            .OrderBy(t => t.StartedAt)
            .ToList();
    }

    private Take? LoadTake(string folderName, string takeId)
    {
        var path = Path.Combine(TakesDir(folderName), takeId, "take.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Take>(File.ReadAllText(path), Json);
    }

    /// <summary>
    /// Reserves a new take folder ("Take 05") and returns its path. The
    /// number is max+1 so deleted takes leave gaps. An abandoned folder from
    /// a cancelled/crashed recording (no take.json) is reused.
    /// </summary>
    public string BeginTake(Scene scene)
    {
        var takesDir = TakesDir(scene.FolderName);
        var n = LoadTakes(scene.FolderName).Select(t => t.Number).DefaultIfEmpty(0).Max() + 1;
        string dir;
        while (true)
        {
            dir = Path.Combine(takesDir, $"Take {n:D2}");
            if (!Directory.Exists(dir)) break;
            if (File.Exists(Path.Combine(dir, "take.json"))) { n++; continue; }
            break; // abandoned: reuse
        }
        Directory.CreateDirectory(dir);
        // Clear partial files left by the abandoned recording, if any.
        foreach (var f in Directory.GetFiles(dir))
        {
            try { File.Delete(f); } catch { }
        }
        return dir;
    }

    /// <summary>Parses the take number out of a "Take 05" folder name.</summary>
    public static int? TakeNumberFromFolder(string folderName)
    {
        var name = folderName.EndsWith("-favorite", StringComparison.Ordinal)
            ? folderName[..^"-favorite".Length] : folderName;
        if (name.StartsWith("Take ", StringComparison.Ordinal) &&
            int.TryParse(name["Take ".Length..], out var n))
            return n;
        return null;
    }

    public void SaveTake(string takeDir, Take take) =>
        WriteJsonAtomic(Path.Combine(takeDir, "take.json"),
            JsonSerializer.Serialize(take, Json));

    public void DeleteTake(string folderName, Take take)
    {
        var dir = Path.Combine(TakesDir(folderName), take.Id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        foreach (var s in LoadScenes().Where(s => s.FolderName == folderName && s.FavoriteTakeId == take.Id))
        {
            s.FavoriteTakeId = null;
            SaveScene(s);
        }
    }

    public string TakeDir(string folderName, string takeId) =>
        Path.Combine(TakesDir(folderName), takeId);

    /// <summary>
    /// Renames a take's folder (used for the "-favorite" suffix). Returns the
    /// id actually in effect: if the move fails the old id is returned.
    /// </summary>
    public string RenameTake(string folderName, string takeId, string newId)
    {
        if (takeId == newId) return takeId;
        var from = TakeDir(folderName, takeId);
        var to = TakeDir(folderName, newId);
        try
        {
            if (!Directory.Exists(from) || Directory.Exists(to)) return takeId;
            Directory.Move(from, to);
            // Keep take.json's Id in sync with the folder name.
            var take = LoadTake(folderName, newId);
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
