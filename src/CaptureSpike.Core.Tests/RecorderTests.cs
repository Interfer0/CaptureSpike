using CaptureSpike.Core.Capture;
using Xunit;

namespace CaptureSpike.Core.Tests;

public sealed class RecorderTests
{
    [Fact]
    public async Task SyntheticTake_RecordsAllSourcesWithSaneOffsets()
    {
        Environment.SetEnvironmentVariable("CAPTURESPIKE_SYNTHETIC", "1");
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "cs-test-" + Path.GetRandomFileName());
            var store = ProjectStore.Create(dir, "Test Project");
            try
            {
                var scene = store.AddScene("Intro");
                Assert.Equal(4, scene.Sources.Count); // screen, webcam, mic, system

                var takeDir = store.BeginTake(scene);
                var recorder = new Recorder(store);
                var ct = TestContext.Current.CancellationToken;
                await using var session = await recorder.StartAsync(store.Project, scene, takeDir, ct: ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                var take = await session.StopAsync(ct);

                Assert.Equal(4, take.Sources.Count);
                Assert.True(take.DurationSeconds >= 3, $"duration was {take.DurationSeconds:F2}s");
                foreach (var s in take.Sources)
                {
                    var path = Path.Combine(takeDir, s.File);
                    Assert.True(File.Exists(path), $"missing {s.File}");
                    Assert.InRange(s.OffsetMs, 0, 8000);
                    Assert.True(new FileInfo(path).Length > 10_000, $"{s.File} suspiciously small");
                }

                // take.json round-trips through the store
                var reloaded = store.LoadTakes(scene.FolderName);
                var rt = Assert.Single(reloaded);
                Assert.Equal(take.Id, rt.Id);
                Assert.Equal(4, rt.Sources.Count);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAPTURESPIKE_SYNTHETIC", null);
        }
    }

    [Fact]
    public void ToolPaths_PrefersExplicitOverride()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            Assert.Equal(tmp, ToolPaths.Resolve("ffmpeg", tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ToolPaths_FindsFfmpegOnPath()
    {
        var found = ToolPaths.Resolve("ffmpeg");
        Assert.True(File.Exists(found), $"resolved path does not exist: {found}");
    }
}
