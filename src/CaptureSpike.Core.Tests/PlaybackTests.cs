using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CaptureSpike.Core.Playback;
using Xunit;

namespace CaptureSpike.Core.Tests;

public sealed class FilterGraphBuilderTests
{
    private static (Project, Scene, Take) SampleTake()
    {
        var project = new Project
        {
            OutputWidth = 1920,
            OutputHeight = 1080,
            Fps = 30,
            Sources =
            [
                new ProjectSource { Id = "screen", Kind = SourceKind.Screen, Label = "Screen" },
                new ProjectSource { Id = "cam", Kind = SourceKind.Webcam, Label = "Webcam" },
                new ProjectSource { Id = "mic", Kind = SourceKind.Microphone, Label = "Mic" },
                new ProjectSource { Id = "sys", Kind = SourceKind.SystemAudio, Label = "System" },
            ],
        };
        var scene = new Scene
        {
            Id = "s1",
            Title = "Intro",
            Sources =
            [
                new SceneSource { SourceId = "screen", X = 0, Y = 0, Width = 1, Height = 1 },
                new SceneSource { SourceId = "cam", X = 0.72, Y = 0.70, Width = 0.26, Height = 0.26 },
            ],
        };
        var take = new Take
        {
            Id = "t1",
            Sources =
            [
                new TakeSource { Id = "screen", Kind = SourceKind.Screen, File = "screen.mp4", OffsetMs = 120 },
                new TakeSource { Id = "cam", Kind = SourceKind.Webcam, File = "cam.mp4", OffsetMs = 80 },
                new TakeSource { Id = "mic", Kind = SourceKind.Microphone, File = "mic.wav", OffsetMs = 50 },
                new TakeSource { Id = "sys", Kind = SourceKind.SystemAudio, File = "sys.wav", OffsetMs = 90 },
            ],
        };
        return (project, scene, take);
    }

    [Fact]
    public void BuildsExpectedGraph()
    {
        var (project, scene, take) = SampleTake();
        var graph = FilterGraphBuilder.Build(project, scene, take);

        // Offsets normalized by the smallest (mic @ 50ms): 70 / 30 / 0 / 40.
        Assert.Equal(
            "[vid1]setpts=PTS+0.070/TB,scale=1920:1080[vbase];" +
            "[vid2]setpts=PTS+0.030/TB,scale=499:281[vpip1];" +
            "[vbase][vpip1]overlay=1382:756[vout1];" +
            "[vout1]null[vo];" +
            "[aid1]adelay=delays=0:all=1,aresample=48000[a0];" +
            "[aid2]adelay=delays=40:all=1,aresample=48000[a1];" +
            "[a0][a1]amix=inputs=2:normalize=0[ao]",
            graph);
    }

    [Fact]
    public void MutedTrackKeepsStableLabels()
    {
        var (project, scene, take) = SampleTake();
        // Mute the screen: webcam must still be addressed as vid2 (labels follow
        // take order, not graph order) and become the full-bleed base.
        var graph = FilterGraphBuilder.Build(project, scene, take, new HashSet<string> { "screen" });

        Assert.Contains("[vid2]setpts=PTS+0.030/TB,scale=1920:1080[vbase]", graph);
        Assert.DoesNotContain("[vid1]", graph);
        Assert.Contains("[aid1]", graph);
        Assert.Contains("[aid2]", graph);
    }

    [Fact]
    public void AudioOnlyTakeHasNoVideoOutput()
    {
        var (project, scene, take) = SampleTake();
        take.Sources = take.Sources.Where(s => s.Kind.IsAudio()).ToList();

        var graph = FilterGraphBuilder.Build(project, scene, take);

        Assert.DoesNotContain("[vo]", graph);
        Assert.Contains("[ao]", graph);
    }

    [Fact]
    public void SingleAudioTrackSkipsAmix()
    {
        var (project, scene, take) = SampleTake();
        take.Sources = take.Sources.Where(s => s.Id == "mic").ToList();

        var graph = FilterGraphBuilder.Build(project, scene, take);

        Assert.Contains("[aid1]adelay=delays=0:all=1,aresample=48000[ao]", graph);
        Assert.DoesNotContain("amix", graph);
    }

    [Fact]
    public void AllMutedThrows()
    {
        var (project, scene, take) = SampleTake();
        var all = take.Sources.Select(s => s.Id).ToHashSet();
        Assert.Throws<InvalidOperationException>(() => FilterGraphBuilder.Build(project, scene, take, all));
    }
}

public sealed class MpvIpcClientTests
{
    private static async Task<(string Path, Task ServerTask)> StartFakeServerAsync(
        Func<StreamReader, StreamWriter, Task> handler)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-mpv-{Guid.NewGuid():N}.sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptAsync();
            await using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            await handler(reader, writer);
            listener.Dispose();
            try { File.Delete(path); } catch { }
        });
        return (path, serverTask);
    }

    [Fact]
    public async Task CommandRoundTrip()
    {
        var (path, server) = await StartFakeServerAsync(async (reader, writer) =>
        {
            var line = await reader.ReadLineAsync();
            using var doc = JsonDocument.Parse(line!);
            var id = doc.RootElement.GetProperty("request_id").GetInt32();
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(new { error = "success", data = 12.5, request_id = id }));
        });

        await using var client = await MpvIpcClient.ConnectUnixAsync(path);
        var data = await client.GetPropertyAsync<double>("time-pos");

        Assert.Equal(12.5, data);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EventRaisedForUntaggedMessage()
    {
        var (path, server) = await StartFakeServerAsync(async (reader, writer) =>
        {
            var line = await reader.ReadLineAsync(); // consume the command
            using var doc = JsonDocument.Parse(line!);
            var id = doc.RootElement.GetProperty("request_id").GetInt32();
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(new { error = "success", data = true, request_id = id }));
            await writer.WriteLineAsync(@"{""event"":""end-file"",""reason"":""eof""}");
        });

        await using var client = await MpvIpcClient.ConnectUnixAsync(path);
        var gotEvent = new TaskCompletionSource<string>();
        client.EventReceived += ev =>
            gotEvent.TrySetResult(ev.GetProperty("event").GetString()!);

        await client.CommandAsync(["get_property", "pause"]);
        var name = await gotEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("end-file", name);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
