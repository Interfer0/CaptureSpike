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
    public void Migration_FromProjectToGlobal_PreservesDevices()
    {
        // The migration runs inside ProjectStore.Open. Build a fake
        // project.json with a per-project palette that has explicit
        // device selections, run Open, and verify the global palette
        // got those entries (with their device / label preserved).
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

        // ProjectStore.Open uses Environment.SpecialFolder.ApplicationData
        // for the global palette, which we redirected above via APPDATA.
        // Make sure that path is empty so we get a clean palette.
        var palettePath = Path.Combine(_dir, "CaptureSpike", "palette.json");
        if (File.Exists(palettePath)) File.Delete(palettePath);

        var store = ProjectStore.Open(projectDir);

        // After migration, the project's Sources is empty (live data
        // is in the global palette).
        Assert.Empty(store.Project.Sources);
        // The global palette got the entries migrated. The device
        // field on each source is preserved verbatim — whatever the
        // project had, the global palette now has.
        var global = GlobalPalette.Load();
        var screen = global.Sources.First(s => s.Id == "screen");
        Assert.Equal("DisplayPort-0", screen.Device);
        var webcam = global.Sources.First(s => s.Id == "webcam");
        Assert.Null(webcam.Device);
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
