// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

using Xunit;

namespace CaptureSpike.Core.Tests;

public sealed class NamingTests
{
    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AddScene_UsesTitleForFolder_AndDedupes()
    {
        var dir = NewTempDir("cs-naming");
        try
        {
            var store = ProjectStore.Create(dir, "Test");
            var a = store.AddScene("Intro");
            var b = store.AddScene("Intro");
            var c = store.AddScene("Intro: Part 1");

            Assert.Equal("Intro", a.FolderName);
            Assert.Equal("Intro 2", b.FolderName);
            Assert.Equal("Intro- Part 1", c.FolderName);
            Assert.True(Directory.Exists(Path.Combine(dir, "scenes", "Intro")));
            Assert.True(Directory.Exists(Path.Combine(dir, "scenes", "Intro 2")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RenameScene_RenamesFolder()
    {
        var dir = NewTempDir("cs-rename-scene");
        try
        {
            var store = ProjectStore.Create(dir, "Test");
            var scene = store.AddScene("Intro");

            Assert.True(store.RenameScene(scene, "Demo Reel"));
            Assert.Equal("Demo Reel", scene.Title);
            Assert.Equal("Demo Reel", scene.FolderName);
            Assert.False(Directory.Exists(Path.Combine(dir, "scenes", "Intro")));
            Assert.True(Directory.Exists(Path.Combine(dir, "scenes", "Demo Reel")));

            // Same sanitized name: title updates, folder stays.
            // "Demo Reel-" sanitizes to "Demo Reel" (trailing '-' is trimmed),
            // so the folder name doesn't change but the title does.
            Assert.True(store.RenameScene(scene, "Demo Reel-"));
            Assert.Equal("Demo Reel-", scene.Title);
            Assert.Equal("Demo Reel", scene.FolderName);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BeginTake_UsesTakeNumberFolders()
    {
        var dir = NewTempDir("cs-takenaming");
        try
        {
            var store = ProjectStore.Create(dir, "Test");
            var scene = store.AddScene("Intro");

            var d1 = store.BeginTake(scene);
            Assert.Equal("Take 01", Path.GetFileName(d1));
            // Reserved but not saved yet: next BeginTake reuses the number.
            var d2 = store.BeginTake(scene);
            Assert.Equal("Take 01", Path.GetFileName(d2));

            // Once saved, the next take increments.
            store.SaveTake(d2, new Take
            {
                Id = "Take 01", SceneId = scene.Id, Number = 1,
                StartedAt = DateTimeOffset.UtcNow,
            });
            var d3 = store.BeginTake(scene);
            Assert.Equal("Take 02", Path.GetFileName(d3));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TakeNumberFromFolder_Parses()
    {
        Assert.Equal(5, ProjectStore.TakeNumberFromFolder("Take 05"));
        Assert.Equal(5, ProjectStore.TakeNumberFromFolder("Take 05-favorite"));
        Assert.Null(ProjectStore.TakeNumberFromFolder("20261004-223015"));
    }

    [Fact]
    public void Open_MigratesLegacyLayoutToNamedFolders()
    {
        var dir = NewTempDir("cs-migrate-naming");
        try
        {
            // Legacy layout: GUID scene folder, timestamp take folder,
            // palette-id media file.
            var sceneDir = Path.Combine(dir, "scenes", "abc123def456");
            var takeDir = Path.Combine(sceneDir, "takes", "20261004-223015-favorite");
            Directory.CreateDirectory(takeDir);
            File.WriteAllText(Path.Combine(dir, "project.json"),
                """{"name":"Mig","sources":[{"id":"mic","kind":"Microphone","label":"Mic","badge":"M1","device":null}]}""");
            File.WriteAllText(Path.Combine(sceneDir, "scene.json"),
                """{"id":"abc123def456","title":"Intro","order":0,"notes":"","favoriteTakeId":"20261004-223015-favorite","sources":[]}""");
            File.WriteAllText(Path.Combine(takeDir, "take.json"),
                """{"id":"20261004-223015-favorite","number":5,"sceneId":"abc123def456","startedAt":"2026-10-04T22:30:15+00:00","durationSeconds":10,"sources":[{"id":"mic","kind":"Microphone","file":"mic.wav","offsetMs":100}]}""");
            File.WriteAllBytes(Path.Combine(takeDir, "mic.wav"), new byte[2048]);

            var store = ProjectStore.Open(dir);

            var scene = store.LoadScenes().Single();
            Assert.Equal("Intro", scene.FolderName);
            Assert.True(Directory.Exists(Path.Combine(dir, "scenes", "Intro")));
            Assert.False(Directory.Exists(sceneDir));

            // FavoriteTakeId remapped to the new take id.
            Assert.Equal("Take 05-favorite", scene.FavoriteTakeId);

            var take = store.LoadTakes("Intro").Single();
            Assert.Equal("Take 05-favorite", take.Id);
            Assert.Equal(5, take.Number);
            var expectedFile = "Mig-Intro-Take 05-M1-Mic.wav";
            Assert.Equal(expectedFile, take.Sources.Single().File);
            Assert.True(File.Exists(Path.Combine(dir, "scenes", "Intro", "takes", "Take 05-favorite", expectedFile)));
            Assert.False(File.Exists(Path.Combine(dir, "scenes", "Intro", "takes", "Take 05-favorite", "mic.wav")));

            // Reopening is stable (idempotent).
            var reopened = ProjectStore.Open(dir);
            Assert.Equal("Take 05-favorite", reopened.LoadScenes().Single().FavoriteTakeId);
            Assert.Equal(expectedFile, reopened.LoadTakes("Intro").Single().Sources.Single().File);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
