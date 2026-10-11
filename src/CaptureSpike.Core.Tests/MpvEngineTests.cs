using CaptureSpike.Core.Playback;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Drives <see cref="MpvPlaybackEngine"/> against a fake mpv: a Python script
/// that creates the IPC socket and answers just enough of the JSON protocol
/// (get/set_property, seek, quit). Validates process lifecycle, IPC connect,
/// position polling, pause/resume, mute-triggered restart and unload.
/// </summary>
public sealed class MpvPlaybackEngineTests
{
    private const string FakeMpvScript = """
        #!/usr/bin/env python3
        import socket, sys, os, json

        sock_path = None
        for a in sys.argv[1:]:
            if a.startswith('--input-ipc-server='):
                sock_path = a.split('=', 1)[1]
        assert sock_path, "no ipc server arg"

        if os.path.exists(sock_path):
            os.unlink(sock_path)
        srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        srv.bind(sock_path)
        srv.listen(1)
        conn, _ = srv.accept()
        f = conn.makefile('rwb')
        pos = 0.0
        paused = False

        def reply(rid, data=None):
            f.write((json.dumps({'error': 'success', 'data': data, 'request_id': rid}) + '\n').encode())
            f.flush()

        def emit_event(event, data=None):
            ev = {'event': event}
            if data is not None:
                ev['data'] = data
            f.write((json.dumps(ev) + '\n').encode())
            f.flush()

        # Send file-loaded right after the client connects so
        # the engine's startup wait completes. Without this
        # the engine treats the missing event as a graph
        # failure (the new fatal-timeout behavior).
        emit_event('file-loaded')

        while True:
            line = f.readline()
            if not line:
                break
            try:
                msg = json.loads(line)
            except Exception:
                continue
            rid = msg.get('request_id')
            cmd = msg.get('command', [])
            if not cmd:
                continue
            if cmd[0] == 'get_property':
                name = cmd[1]
                if name == 'time-pos':
                    if not paused:
                        pos += 0.25
                    reply(rid, pos)
                elif name == 'pause':
                    reply(rid, paused)
                elif name == 'duration':
                    reply(rid, 47.0)
                else:
                    reply(rid, None)
            elif cmd[0] == 'set_property':
                if cmd[1] == 'pause':
                    paused = bool(cmd[2])
                reply(rid)
            elif cmd[0] == 'seek':
                pos = float(cmd[1])
                reply(rid)
            elif cmd[0] == 'quit':
                reply(rid)
                break
            else:
                reply(rid)
        conn.close()
        srv.close()
        try:
            os.unlink(sock_path)
        except Exception:
            pass
        """;

    [Fact]
    public async Task EngineLifecycleAgainstFakeMpv()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fake-mpv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "mpv");
            await File.WriteAllTextAsync(script, FakeMpvScript);
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var takeDir = Path.Combine(dir, "take");
            Directory.CreateDirectory(takeDir);
            foreach (var f in new[] { "screen.mp4", "cam.mp4", "mic.wav", "sys.wav" })
                await File.WriteAllTextAsync(Path.Combine(takeDir, f), "dummy");

            var (project, scene, take) = FilterGraphBuilderTests.SampleTake();
            take.DurationSeconds = 47;

            await using var engine = new MpvPlaybackEngine(script);
            await engine.LoadAsync(project, scene, takeDir, take);
            Assert.True(engine.IsPlaying);

            await Task.Delay(1200);
            Assert.True(engine.Position.TotalSeconds > 0);

            await engine.SeekAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(700);
            Assert.InRange(engine.Position.TotalSeconds, 8, 13);

            await engine.PauseAsync();
            await Task.Delay(600);
            Assert.False(engine.IsPlaying);
            await engine.PlayAsync();

            // Muting rebuilds the graph and restarts mpv at the same position.
            await engine.SetTrackMutedAsync("mic", true);
            await Task.Delay(1800);
            Assert.True(engine.IsPlaying);
            Assert.InRange(engine.Position.TotalSeconds, 8, 14);

            await engine.UnloadAsync();
            Assert.False(engine.IsPlaying);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
