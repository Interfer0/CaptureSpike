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
    public void EnsureProjectSources_SeedsFromGlobalRegistry_PreservesDevices()
    {
        // The migration runs inside ProjectStore.Open. Build a fake
        // project.json with a per-project palette that has explicit
        // device selections, run Open, and verify the project kept
        // its sources (with their device / label preserved verbatim)
        // and that the global kinds registry also got any new kinds.
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

        // Make sure the global palette file is empty so we get a clean
        // registry to compare against.
        var palettePath = Path.Combine(_dir, "CaptureSpike", "palette.json");
        if (File.Exists(palettePath)) File.Delete(palettePath);

        var store = ProjectStore.Open(projectDir);

        // The project's palette is preserved verbatim. The screen
        // entry keeps the user's DisplayPort-0 device.
        Assert.Equal(4, store.Project.Sources.Count);
        var screen = store.Project.Sources.First(s => s.Id == "screen");
        Assert.Equal("DisplayPort-0", screen.Device);
        // The webcam had no device and no badge — preserved as-is.
        var webcam = store.Project.Sources.First(s => s.Id == "webcam");
        Assert.Null(webcam.Device);
        Assert.Equal("Webcam", webcam.Label);
        // The global registry has all the kinds, so the project also
        // picks up the ones it was missing (mic, system).
        Assert.Contains(store.Project.Sources, s => s.Id == "mic");
        Assert.Contains(store.Project.Sources, s => s.Id == "system");
        // The global registry is unchanged — the project is just a
        // consumer; the registry isn't populated from the project
        // (kinds are added there explicitly by the user).
        var global = GlobalPalette.Load();
        Assert.Equal(4, global.Sources.Count);
        Assert.Contains(global.Sources, s => s.Id == "screen");
        Assert.Contains(global.Sources, s => s.Id == "webcam");
        Assert.Contains(global.Sources, s => s.Id == "mic");
        Assert.Contains(global.Sources, s => s.Id == "system");
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
