using Xunit;

namespace CaptureSpike.Core.Tests;

public sealed class RenameTakeTests
{
    [Fact]
    public void RenameTake_MovesFolderAndSyncsTakeJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cs-rename-{Guid.NewGuid():N}");
        try
        {
            var store = ProjectStore.Create(dir, "Rename Test");
            var scene = store.AddScene("Intro");
            var takeDir = store.BeginTake(scene);
            var take = new Take
            {
                Id = Path.GetFileName(takeDir),
                SceneId = scene.Id,
                Number = 1,
                StartedAt = DateTimeOffset.UtcNow,
            };
            store.SaveTake(takeDir, take);

            var newId = store.RenameTake(scene.FolderName, take.Id, take.Id + "-favorite");

            Assert.Equal(take.Id + "-favorite", newId);
            Assert.False(Directory.Exists(takeDir));
            Assert.True(Directory.Exists(store.TakeDir(scene.FolderName, newId)));
            var reloaded = store.LoadTakes(scene.FolderName).Single();
            Assert.Equal(newId, reloaded.Id); // take.json Id matches the folder
            Assert.Equal(1, reloaded.Number);

            // Renaming back works too.
            var backId = store.RenameTake(scene.FolderName, newId, take.Id);
            Assert.Equal(take.Id, backId);
            Assert.Equal(take.Id, store.LoadTakes(scene.FolderName).Single().Id);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RenameTake_CollidingTargetKeepsOldId()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cs-rename2-{Guid.NewGuid():N}");
        try
        {
            var store = ProjectStore.Create(dir, "Rename Collision");
            var scene = store.AddScene("Intro");
            var takeDir = store.BeginTake(scene);
            var take = new Take { Id = Path.GetFileName(takeDir), SceneId = scene.Id };
            store.SaveTake(takeDir, take);
            // Occupy the target name first.
            Directory.CreateDirectory(store.TakeDir(scene.FolderName, "taken"));

            Assert.Equal(take.Id, store.RenameTake(scene.FolderName, take.Id, "taken"));
            Assert.True(Directory.Exists(takeDir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
