// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using Xunit;

namespace CaptureSpike.Core.Tests;

[Collection(nameof(GlobalPaletteCollection))]
public sealed class StoreTests
{
    [Fact]
    public void LegacyProjectMigratesToPalette()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cs-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "scenes", "s1", "takes"));
        // project.json from before the palette existed (no "sources").
        File.WriteAllText(Path.Combine(dir, "project.json"),
            """{"name":"Old","outputWidth":1920,"outputHeight":1080,"fps":30}""");
        // scene.json with the old embedded-source shape.
        File.WriteAllText(Path.Combine(dir, "scenes", "s1", "scene.json"),
            """{"id":"s1","title":"Intro","order":0,"notes":"","favoriteTakeId":null,"sources":[{"id":"screen","kind":"Screen","enabled":true,"device":null,"x":0,"y":0,"width":1,"height":1},{"id":"mic","kind":"Microphone","enabled":false,"device":null,"x":0,"y":0,"width":1,"height":1}]}""");

        var store = ProjectStore.Open(dir);

        // MigrateLegacyScene reads the embedded sources from the
        // scene and adds them to Project.Sources (the project's
        // import list) so the scene can reference them by id.
        // That's the project's own import list being managed
        // during a legacy migration, NOT the global palette being
        // touched. The project has the screen + mic it was using.
        Assert.Equal(2, store.Project.Sources.Count);
        Assert.Contains(store.Project.Sources, s => s.Id == "screen");
        Assert.Contains(store.Project.Sources, s => s.Id == "mic");
        var scene = store.LoadScenes().Single();
        // The legacy scene had embedded sources (id, kind,
        // device, x/y/w/h). MigrateLegacyScene rewrote it to
        // reference palette ids — same 2 sources, now with
        // sourceId set and the embedded kind/device stripped.
        Assert.Equal(2, scene.Sources.Count);
        Assert.Equal("screen", scene.Sources[0].SourceId);
        Assert.True(scene.Sources[0].Enabled);
        Assert.Equal("mic", scene.Sources[1].SourceId);
        Assert.False(scene.Sources[1].Enabled);

        // Reopening is stable: no duplicate palette entries, no re-migration.
        var reopened = ProjectStore.Open(dir);
        Assert.Equal(2, reopened.Project.Sources.Count);
        Assert.Equal(2, reopened.LoadScenes().Single().Sources.Count);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void NewProjectStartsEmpty_AndDoesNotTouchGlobal()
    {
        // Per Daniel: 'the projects should not touch the sources ever'.
        // Creating a new project must NOT populate its own
        // Project.Sources, must NOT seed from the global palette,
        // and must NOT modify the global palette. The user manages
        // the global directly via the 'Available sources' panel.
        // The project has its own import list (Project.Sources) that
        // stays empty until the user imports sources via the
        // scene's '+ Add' picker.
        //
        // Use an isolated palette path so leftover state from other
        // tests doesn't pollute the assertion. GlobalPalette.FilePath
        // honours the CAPTURESPIKE_PALETTE_DIR env var.
        var paletteDir = Path.Combine(Path.GetTempPath(), $"cs-newpal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(paletteDir);
        var previous = System.Environment.GetEnvironmentVariable("CAPTURESPIKE_PALETTE_DIR");
        System.Environment.SetEnvironmentVariable("CAPTURESPIKE_PALETTE_DIR", paletteDir);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), $"cs-new-{Guid.NewGuid():N}");
            var store = ProjectStore.Create(dir, "Test");

            // Project.Sources starts empty. The user imports sources
            // explicitly.
            Assert.Empty(store.Project.Sources);

            // The global palette file was not created by Create, and
            // the in-memory catalog is unchanged from before. The
            // "default" 4-kind global is the in-memory state when
            // no file exists — that's fine. The point is Create
            // didn't WRITE the file or modify anything.
            var globalBefore = GlobalPalette.Load();
            var sourcesBefore = globalBefore.Sources.Count;
            var fileExistedBefore = File.Exists(GlobalPalette.FilePath);
            // Do a no-op Create to confirm it doesn't touch the global.
            var dir2 = Path.Combine(Path.GetTempPath(), $"cs-new2-{Guid.NewGuid():N}");
            var _ = ProjectStore.Create(dir2, "Test2");
            var globalAfter = GlobalPalette.Load();
            Assert.Equal(sourcesBefore, globalAfter.Sources.Count);
            Assert.Equal(fileExistedBefore, File.Exists(GlobalPalette.FilePath));

            // Adding a scene still works. The scene has screen + mic
            // wired by default — they come from the global palette,
            // not from the project's imports. The user adds more
            // sources via the scene's "+ Add" picker as needed.
            var scene = store.AddScene("Intro");
            Assert.Equal(2, scene.Sources.Count);
            Assert.Contains(scene.Sources, s => s.SourceId == "screen");
            Assert.Contains(scene.Sources, s => s.SourceId == "mic");

            Directory.Delete(dir, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("CAPTURESPIKE_PALETTE_DIR", previous);
            try { Directory.Delete(paletteDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RenameProject_PersistsAndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cs-rename-{Guid.NewGuid():N}");
        try
        {
            var store = ProjectStore.Create(dir, "Original name");
            Assert.Equal("Original name", store.Project.Name);

            // Same path the VM's RenameProjectAtAsync uses: mutate the
            // model object, call SaveProject. Folder name is intentionally
            // untouched — the in-app rename doesn't move files on disk.
            store.Project.Name = "Renamed tutorial";
            store.SaveProject();

            // Round-trip via a fresh store handle so we know it really
            // hit disk rather than just sitting on the in-memory copy.
            var reopened = ProjectStore.Open(dir);
            Assert.Equal("Renamed tutorial", reopened.Project.Name);
            Assert.Equal(dir, store.ProjectDir); // folder name didn't move
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
