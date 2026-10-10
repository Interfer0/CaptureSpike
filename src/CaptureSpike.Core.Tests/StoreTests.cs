using Xunit;

namespace CaptureSpike.Core.Tests;

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

        Assert.Equal(4, store.Project.Sources.Count); // default palette seeded
        var scene = store.LoadScenes().Single();
        Assert.Equal(2, scene.Sources.Count);
        Assert.Equal("screen", scene.Sources[0].SourceId);
        Assert.True(scene.Sources[0].Enabled);
        Assert.Equal("mic", scene.Sources[1].SourceId);
        Assert.False(scene.Sources[1].Enabled);

        // Reopening is stable: no duplicate palette entries, no re-migration.
        var reopened = ProjectStore.Open(dir);
        Assert.Equal(4, reopened.Project.Sources.Count);
        Assert.Equal(2, reopened.LoadScenes().Single().Sources.Count);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void NewProjectSeedsPaletteAndSceneRefs()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cs-new-{Guid.NewGuid():N}");
        var store = ProjectStore.Create(dir, "Test");
        var scene = store.AddScene("Intro");

        Assert.Equal(4, store.Project.Sources.Count);
        Assert.Equal(4, scene.Sources.Count);
        Assert.All(scene.Sources,
            s => Assert.Contains(store.Project.Sources, p => p.Id == s.SourceId));

        Directory.Delete(dir, recursive: true);
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
