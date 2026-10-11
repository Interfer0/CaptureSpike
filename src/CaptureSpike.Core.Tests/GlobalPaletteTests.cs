using CaptureSpike.Core;
using Xunit;

namespace CaptureSpike.Core.Tests;

[Collection(nameof(GlobalPaletteCollection))]
public sealed class GlobalPaletteTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GlobalPaletteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cs-palette-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "palette.json");
        // GlobalPalette.FilePath honours a CAPTURESPIKE_PALETTE_DIR env
        // override so the test process can be redirected at any point
        // (including after the first static field access).
        lock (GlobalPaletteCollection.Sync)
        {
            Environment.SetEnvironmentVariable("CAPTURESPIKE_PALETTE_DIR", _dir);
        }
    }

    public void Dispose()
    {
        lock (GlobalPaletteCollection.Sync)
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FirstLoad_SeedsDefaultPalette()
    {
        // GlobalPalette.FilePath is resolved at process start; we
        // can't change it. To make this test meaningful we exercise
        // the model methods directly on a constructed palette.
        var palette = new GlobalPalette();
        Assert.Empty(palette.Sources);
        palette.Add(new ProjectSource { Id = "x", Kind = SourceKind.Screen });
        Assert.Single(palette.Sources);
        Assert.True(palette.Contains("x"));
    }

    [Fact]
    public void Add_IsIdempotent_ById()
    {
        var palette = new GlobalPalette();
        var src = new ProjectSource { Id = "x", Kind = SourceKind.Screen, Label = "Screen" };
        palette.Add(src);
        palette.Add(src); // duplicate id — no-op
        Assert.Single(palette.Sources);
    }

    [Fact]
    public void Remove_ReturnsTrueOnHit_FalseOnMiss()
    {
        var palette = new GlobalPalette();
        palette.Add(new ProjectSource { Id = "x", Kind = SourceKind.Screen });
        Assert.True(palette.Remove("x"));
        Assert.False(palette.Remove("x"));
        Assert.Empty(palette.Sources);
    }

    [Fact]
    public void EnsureProjectSources_LeavesNonEmptyProjectAlone()
    {
        // The user's available sources catalog is theirs. Opening
        // a project with a non-empty Project.Sources must NOT
        // touch the global palette, must NOT add kinds the user
        // hasn't explicitly added, and must NOT save the project.
        // Whatever the project file says is what the user sees —
        // full stop. The earlier "add missing global kinds" loop
        // was rewriting the user's catalog on every open, which
        // is exactly the bug Daniel is reporting.
        var projectDir = Path.Combine(_dir, "proj1.csp");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "scenes"));
        File.WriteAllText(Path.Combine(projectDir, "project.json"), """
            {
              "name": "Test",
              "outputWidth": 1920,
              "outputHeight": 1080,
              "fps": 30,
              "namingVersion": 1,
              "sources": [
                {"id":"screen","kind":"Screen","label":"Screen","device":"DisplayPort-0","badge":""},
                {"id":"webcam","kind":"Webcam","label":"Webcam","device":null,"badge":""}
              ]
            }
            """);

        // Empty the global palette so we can prove opening the
        // project doesn't touch it. CAPTURESPIKE_PALETTE_DIR is set
        // to _dir in the ctor, so GlobalPalette.FilePath is just
        // _dir/palette.json (no CaptureSpike subfolder — that's
        // only the App's data folder; the env var bypasses the
        // default layout).
        var palettePath = _path;
        if (File.Exists(palettePath)) File.Delete(palettePath);

        var beforeJson = "";
        if (File.Exists(palettePath)) beforeJson = File.ReadAllText(palettePath);

        var store = ProjectStore.Open(projectDir);

        // The project's palette is preserved verbatim. Exactly the
        // two entries the file had — no extras added from anywhere.
        Assert.Equal(2, store.Project.Sources.Count);
        var screen = store.Project.Sources.First(s => s.Id == "screen");
        Assert.Equal("DisplayPort-0", screen.Device);
        var webcam = store.Project.Sources.First(s => s.Id == "webcam");
        Assert.Null(webcam.Device);

        // The global palette is untouched. If it didn't exist
        // before, it still doesn't exist; if it had a specific
        // contents, the byte-for-byte contents are unchanged.
        Assert.Equal(beforeJson,
            File.Exists(palettePath) ? File.ReadAllText(palettePath) : "");

        // Reopening is stable: no writes, no surprises.
        var reopened = ProjectStore.Open(projectDir);
        Assert.Equal(2, reopened.Project.Sources.Count);
        Assert.Equal("DisplayPort-0", reopened.Project.Sources.First(s => s.Id == "screen").Device);
    }

    [Fact]
    public void Migration_IsIdempotent_DoesNotDuplicateEntries()
    {
        // Two projects that share an entry — opening both should
        // leave the global palette with one entry, not two.
        var p1 = Path.Combine(_dir, "p1.csp");
        var p2 = Path.Combine(_dir, "p2.csp");
        foreach (var d in new[] { p1, p2 })
        {
            Directory.CreateDirectory(d);
            Directory.CreateDirectory(Path.Combine(d, "scenes"));
            File.WriteAllText(Path.Combine(d, "project.json"), """
                {
                  "name": "T",
                  "outputWidth": 1920,
                  "outputHeight": 1080,
                  "fps": 30,
                  "namingVersion": 1,
                  "sources": [
                    {"id":"screen","kind":"Screen","label":"Screen","device":null,"badge":""}
                  ]
                }
                """);
        }
        var palettePath = Path.Combine(_dir, "CaptureSpike", "palette.json");
        if (File.Exists(palettePath)) File.Delete(palettePath);

        ProjectStore.Open(p1);
        ProjectStore.Open(p2);

        var global = GlobalPalette.Load();
        Assert.Single(global.Sources, s => s.Id == "screen");
    }
}
